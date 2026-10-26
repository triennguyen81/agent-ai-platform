"""Unit tests for aiq_agent.documents.extraction — no FastAPI/HTTP involved,
same philosophy as test_ocr_pipeline.py (test the pure logic directly)."""

from __future__ import annotations

import io

import pytest

from aiq_agent.documents.extraction import (
    DocumentExtractionError,
    extract_document_text,
)

pytest.importorskip("pypdfium2")


def _minimal_text_pdf(text: str) -> bytes:
    """Hand-built, byte-exact minimal single-page PDF with a real text-layer
    content stream (BT/Tj) — avoids taking a dependency on a PDF-authoring
    library just for test fixtures."""

    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> "
        b"/MediaBox [0 0 300 300] /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    stream = f"BT /F1 12 Tf 20 250 Td ({text}) Tj ET".encode("latin-1")
    objects.append(
        b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream"
    )

    out = io.BytesIO()
    out.write(b"%PDF-1.4\n")
    offsets: list[int] = []
    for i, obj in enumerate(objects, start=1):
        offsets.append(out.tell())
        out.write(f"{i} 0 obj\n".encode())
        out.write(obj)
        out.write(b"\nendobj\n")
    xref_offset = out.tell()
    n = len(objects) + 1
    out.write(f"xref\n0 {n}\n".encode())
    out.write(b"0000000000 65535 f \n")
    for off in offsets:
        out.write(f"{off:010d} 00000 n \n".encode())
    out.write(b"trailer\n")
    out.write(f"<< /Size {n} /Root 1 0 R >>\n".encode())
    out.write(b"startxref\n")
    out.write(f"{xref_offset}\n".encode())
    out.write(b"%%EOF")
    return out.getvalue()


def _blank_pdf() -> bytes:
    """A valid single-page PDF with NO content stream at all — simulates a
    scanned PDF that has no embedded text layer to extract."""

    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /Resources << >> /MediaBox [0 0 300 300] >>",
    ]
    out = io.BytesIO()
    out.write(b"%PDF-1.4\n")
    offsets: list[int] = []
    for i, obj in enumerate(objects, start=1):
        offsets.append(out.tell())
        out.write(f"{i} 0 obj\n".encode())
        out.write(obj)
        out.write(b"\nendobj\n")
    xref_offset = out.tell()
    n = len(objects) + 1
    out.write(f"xref\n0 {n}\n".encode())
    out.write(b"0000000000 65535 f \n")
    for off in offsets:
        out.write(f"{off:010d} 00000 n \n".encode())
    out.write(b"trailer\n")
    out.write(f"<< /Size {n} /Root 1 0 R >>\n".encode())
    out.write(b"startxref\n")
    out.write(f"{xref_offset}\n".encode())
    out.write(b"%%EOF")
    return out.getvalue()


def _minimal_png() -> bytes:
    """A real, validly-encoded tiny PNG — built via Pillow (already a
    dependency of the OCR extras this test image ships) rather than a
    hand-typed byte string, which is too easy to get subtly wrong."""

    PIL_Image = pytest.importorskip("PIL.Image")
    buf = io.BytesIO()
    PIL_Image.new("RGB", (4, 4), color=(255, 255, 255)).save(buf, format="PNG")
    return buf.getvalue()


class _StubPipeline:
    """Minimal OcrPipelineLike stub — records calls, returns fixed text."""

    def __init__(self, text: str) -> None:
        self._text = text
        self.calls: list[list[bytes]] = []

    async def run_pages(self, page_images: list[bytes]):
        self.calls.append(list(page_images))
        return _StubResult(self._text)


class _StubResult:
    def __init__(self, text: str) -> None:
        self._text = text

    def joined_text(self) -> str:
        return self._text


@pytest.mark.asyncio
async def test_pdf_with_text_layer_extracts_directly_without_ocr() -> None:
    pdf_bytes = _minimal_text_pdf("Hop dong dich vu ke toan so 04 nam 2026 giua ben A va ben B")
    pipeline = _StubPipeline("SHOULD NOT BE CALLED")

    result = await extract_document_text(filename="contract.pdf", data=pdf_bytes, pipeline=pipeline)

    assert result.extraction_method == "pdf_text_layer"
    assert "Hop dong dich vu ke toan" in result.text
    assert result.mime_type == "application/pdf"
    assert pipeline.calls == []  # OCR must never run for a text-layer PDF


@pytest.mark.asyncio
async def test_scanned_pdf_falls_back_to_ocr_pipeline() -> None:
    pdf_bytes = _blank_pdf()
    pipeline = _StubPipeline("Nội dung OCR từ trang quét")

    result = await extract_document_text(filename="scanned.pdf", data=pdf_bytes, pipeline=pipeline)

    assert result.extraction_method == "pdf_ocr"
    assert result.text == "Nội dung OCR từ trang quét"
    assert len(pipeline.calls) == 1  # one call, with the rendered page(s)


@pytest.mark.asyncio
async def test_scanned_pdf_without_pipeline_configured_raises_503() -> None:
    pdf_bytes = _blank_pdf()

    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="scanned.pdf", data=pdf_bytes, pipeline=None)

    assert exc_info.value.status_code == 503


@pytest.mark.asyncio
async def test_docx_extracts_paragraphs_and_table_cells() -> None:
    docx = pytest.importorskip("docx")
    document = docx.Document()
    document.add_paragraph("Điều 1 — Nội dung dịch vụ")
    table = document.add_table(rows=1, cols=2)
    table.rows[0].cells[0].text = "Bên A"
    table.rows[0].cells[1].text = "Công ty TNHH Cát Tường"
    buf = io.BytesIO()
    document.save(buf)

    result = await extract_document_text(
        filename="contract.docx",
        data=buf.getvalue(),
        pipeline=None,
    )

    assert result.extraction_method == "docx_text"
    assert "Điều 1" in result.text
    assert "Công ty TNHH Cát Tường" in result.text


@pytest.mark.asyncio
async def test_image_routes_through_ocr_pipeline() -> None:
    pipeline = _StubPipeline("Text OCR'd from image")

    result = await extract_document_text(
        filename="scan.png", data=_minimal_png(), pipeline=pipeline
    )

    assert result.extraction_method == "image_ocr"
    assert result.text == "Text OCR'd from image"
    assert len(pipeline.calls) == 1


@pytest.mark.asyncio
async def test_legacy_doc_format_is_rejected_with_clear_message() -> None:
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="old.doc", data=b"anything", pipeline=None)

    assert exc_info.value.status_code == 415
    assert ".docx" in str(exc_info.value)


@pytest.mark.asyncio
async def test_unsupported_extension_is_rejected() -> None:
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="notes.txt", data=b"plain text file", pipeline=None)

    assert exc_info.value.status_code == 415


@pytest.mark.asyncio
async def test_empty_ocr_result_is_rejected_as_extraction_failure() -> None:
    pipeline = _StubPipeline("   ")  # whitespace-only — simulates OCR finding nothing

    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="blank.png", data=_minimal_png(), pipeline=pipeline)

    assert exc_info.value.status_code == 422


@pytest.mark.asyncio
async def test_malformed_pdf_bytes_are_rejected_not_crashed_on() -> None:
    # Starts with the PDF magic bytes but is not a parseable PDF.
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(
            filename="broken.pdf", data=b"%PDF-1.4\ngarbage not a real pdf", pipeline=None
        )

    assert exc_info.value.status_code == 422


@pytest.mark.asyncio
async def test_malformed_docx_bytes_are_rejected_not_crashed_on() -> None:
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(
            filename="broken.docx", data=b"PK\x03\x04not a real docx zip", pipeline=None
        )

    assert exc_info.value.status_code == 422


_SAMPLE_INVOICE_XML = b"""<?xml version="1.0" encoding="UTF-8"?>
<HDon xmlns:ns="http://vietinvoice.example/invoice">
  <TTChung>
    <SoHDon>00012345</SoHDon>
    <NgayLap>2026-06-15</NgayLap>
  </TTChung>
  <NDHDon>
    <NBan Ten="Cong ty TNHH Cat Tuong AI">
      <MST>0399999999</MST>
    </NBan>
    <NMua>
      <Ten>Cong ty TNHH Dich Vu Ke Toan XYZ</Ten>
      <MST>0312345678</MST>
    </NMua>
    <TToan>
      <TgTTTBSo>24000000</TgTTTBSo>
    </TToan>
  </NDHDon>
</HDon>
"""


@pytest.mark.asyncio
async def test_xml_invoice_is_flattened_to_readable_text() -> None:
    result = await extract_document_text(
        filename="invoice.xml", data=_SAMPLE_INVOICE_XML, pipeline=None
    )

    assert result.extraction_method == "xml_text"
    assert result.mime_type == "application/xml"
    # Schema-agnostic flattening — tag/attribute names carried through
    # verbatim as "label: value" lines, namespace prefix stripped.
    assert "SoHDon: 00012345" in result.text
    assert "NgayLap: 2026-06-15" in result.text
    assert "MST: 0399999999" in result.text
    assert "MST: 0312345678" in result.text
    assert "NBan.Ten: Cong ty TNHH Cat Tuong AI" in result.text
    assert "TgTTTBSo: 24000000" in result.text


@pytest.mark.asyncio
async def test_xml_detected_by_declaration_even_without_xml_extension() -> None:
    # Some e-invoice export tools hand back a .txt/no-extension download —
    # sniff the <?xml declaration rather than trusting the filename alone.
    result = await extract_document_text(filename="export", data=_SAMPLE_INVOICE_XML, pipeline=None)

    assert result.extraction_method == "xml_text"


@pytest.mark.asyncio
async def test_malformed_xml_is_rejected_not_crashed_on() -> None:
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(
            filename="broken.xml", data=b"<HDon><TTChung>not closed", pipeline=None
        )

    assert exc_info.value.status_code == 422


@pytest.mark.asyncio
async def test_xml_with_doctype_is_rejected_as_untrusted_input() -> None:
    # Untrusted upload — DOCTYPE/ENTITY (XXE / billion-laughs vectors) must
    # be rejected outright, never parsed.
    payload = b'<?xml version="1.0"?><!DOCTYPE HDon [<!ENTITY x "boom">]><HDon>&x;</HDon>'
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="malicious.xml", data=payload, pipeline=None)

    assert exc_info.value.status_code == 415


@pytest.mark.asyncio
async def test_empty_xml_content_is_rejected_as_extraction_failure() -> None:
    with pytest.raises(DocumentExtractionError) as exc_info:
        await extract_document_text(filename="empty.xml", data=b"<HDon></HDon>", pipeline=None)

    assert exc_info.value.status_code == 422
