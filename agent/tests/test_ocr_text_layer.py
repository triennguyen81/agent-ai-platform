"""OCR performance audit: text-layer-first PDFs and no-thinking correction."""

from __future__ import annotations

import io
from typing import Any

import pytest

pdfium = pytest.importorskip("pypdfium2")
Image = pytest.importorskip("PIL.Image")

from aiq_agent.ocr import OcrPipeline, PassthroughValidator, StubOcrExtractor  # noqa: E402
from aiq_agent.ocr.corrector import TextCorrector  # noqa: E402
from aiq_agent.ocr.pdf import analyse_pdf_pages, usable_text_layer  # noqa: E402

INVOICE_TEXT = "HOA DON GIA TRI GIA TANG So 0001026 Tong cong thanh toan 33000000 MST 0200000001"


def _text_pdf(lines: list[str]) -> bytes:
    """Minimal single-page PDF with a real text layer (Helvetica)."""
    ops = ["BT", "/F1 11 Tf", "50 780 Td", "14 TL"] + [f"({line}) Tj T*" for line in lines] + ["ET"]
    stream = "\n".join(ops).encode("latin-1")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 "
        b"0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
    ]
    out = io.BytesIO()
    out.write(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, 1):
        offsets.append(out.tell())
        out.write(b"%d 0 obj\n" % number + body + b"\nendobj\n")
    xref = out.tell()
    out.write(b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1))
    for offset in offsets:
        out.write(b"%010d 00000 n \n" % offset)
    out.write(
        b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    )
    return out.getvalue()


def _scan_pdf() -> bytes:
    """A 'scanned' page: one full-page image, no text layer."""
    image = Image.new("RGB", (620, 877), "white")
    buffer = io.BytesIO()
    image.save(buffer, format="PDF")
    return buffer.getvalue()


def _merge(*documents: bytes) -> bytes:
    merged = pdfium.PdfDocument.new()
    for data in documents:
        merged.import_pages(pdfium.PdfDocument(data))
    buffer = io.BytesIO()
    merged.save(buffer)
    return buffer.getvalue()


def test_usable_text_layer_rules() -> None:
    assert usable_text_layer(INVOICE_TEXT)
    assert not usable_text_layer("Trang 1")  # too short
    assert not usable_text_layer("�" * 10 + " " + "x" * 60)  # replacement characters
    assert not usable_text_layer("·" * 30 + " " + "-" * 30)  # mostly punctuation
    assert usable_text_layer("DỮ LIỆU MẪU – HOÁ ĐƠN GIÁ TRỊ GIA TĂNG số 0001026 tổng 33.000.000")


def test_digital_page_uses_text_layer_and_is_not_rendered() -> None:
    [page] = analyse_pdf_pages(_text_pdf([INVOICE_TEXT]))
    assert page["use_text_layer"] and page["png"] is None
    assert "0001026" in page["text"] and page["image_coverage"] == 0.0


def test_scanned_page_is_always_rendered_for_ocr() -> None:
    [page] = analyse_pdf_pages(_scan_pdf())
    assert not page["use_text_layer"] and page["png"] is not None
    assert page["image_coverage"] > 0.99


def test_scan_with_digital_overlay_text_is_still_ocrd() -> None:
    """A scan carrying a small digital header must not skip OCR of its image."""
    scan = pdfium.PdfDocument(_scan_pdf())
    page = scan[0]
    text_pdf = pdfium.PdfDocument(_text_pdf([INVOICE_TEXT]))
    form = text_pdf.page_as_xobject(0, scan)
    page.insert_obj(form.as_pageobject())
    page.gen_content()
    buffer = io.BytesIO()
    scan.save(buffer)
    [analysed] = analyse_pdf_pages(buffer.getvalue())
    assert usable_text_layer(analysed["text"])  # the overlay text alone looks fine…
    assert not analysed["use_text_layer"]  # …but the page is a scan → OCR


class _CountingCorrector(TextCorrector):
    def __init__(self) -> None:
        self.calls: list[str] = []

    async def correct(self, text: str) -> str:
        self.calls.append(text)
        return text.upper()


@pytest.mark.asyncio
async def test_mixed_pdf_ocrs_only_scanned_pages_and_keeps_page_order() -> None:
    data = _merge(_text_pdf([INVOICE_TEXT]), _scan_pdf(), _text_pdf(["Trang ba: " + INVOICE_TEXT]))
    corrector = _CountingCorrector()
    pipeline = OcrPipeline(
        extractor=StubOcrExtractor(["chu tren anh scan"]),
        corrector=corrector,
        validator=PassthroughValidator(),
        text_layer_first=True,
    )

    result = await pipeline.run_pdf(data)

    assert [p.source for p in result.pages] == ["text_layer", "ocr", "text_layer"]
    assert [p.page_number for p in result.pages] == [1, 2, 3]
    assert corrector.calls == ["chu tren anh scan"]  # only the scanned page is corrected
    assert result.corrected_text_by_page[1] == "CHU TREN ANH SCAN"
    assert "0001026" in result.corrected_text_by_page[0] and result.pages[0].confidence == 1.0
    assert [v.page_number for v in result.validation] == [1, 2, 3]


@pytest.mark.asyncio
async def test_text_layer_first_off_keeps_the_old_full_ocr_path() -> None:
    corrector = _CountingCorrector()
    pipeline = OcrPipeline(
        extractor=StubOcrExtractor(["ocr text"]),
        corrector=corrector,
        validator=PassthroughValidator(),
    )
    result = await pipeline.run_pdf(_text_pdf([INVOICE_TEXT]))
    assert [p.source for p in result.pages] == ["ocr"] and corrector.calls == ["ocr text"]


@pytest.mark.asyncio
async def test_fully_digital_pdf_makes_no_llm_call() -> None:
    corrector = _CountingCorrector()
    pipeline = OcrPipeline(
        extractor=StubOcrExtractor([]),
        corrector=corrector,
        validator=PassthroughValidator(),
        text_layer_first=True,
    )
    result = await pipeline.run_pdf(_text_pdf([INVOICE_TEXT]))
    assert corrector.calls == [] and result.llm_usage is None


class _OptionsClient:
    captured: list[Any] = []

    def __init__(self, **kwargs: Any) -> None:
        self.captured.append(kwargs.get("options"))

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    async def query(self, *_a: Any, **_k: Any) -> None:
        return None

    async def receive_response(self):
        from claude_agent_sdk import AssistantMessage, TextBlock

        yield AssistantMessage(content=[TextBlock(text="ok")], model="m")


@pytest.mark.asyncio
async def test_correction_client_disables_thinking_chat_keeps_default(monkeypatch) -> None:
    from aiq_agent.llm import claude_cli
    from aiq_agent.llm.base import ChatMessage

    _OptionsClient.captured = []
    monkeypatch.setattr(claude_cli, "ClaudeSDKClient", _OptionsClient)
    await claude_cli.ClaudeCliLlmService(model="m", disable_thinking=True).chat(
        [ChatMessage("user", "x")]
    )
    await claude_cli.ClaudeCliLlmService(model="m").chat([ChatMessage("user", "x")])
    no_thinking, default = _OptionsClient.captured
    assert no_thinking.thinking == {"type": "disabled"} and no_thinking.tools == []
    assert default.thinking is None


def test_performance_settings_defaults() -> None:
    from aiq_agent.config import Settings

    settings = Settings(embedding_enabled=False)
    assert settings.ocr_pdf_text_layer_first is True
    assert settings.ocr_correction_disable_thinking is True
