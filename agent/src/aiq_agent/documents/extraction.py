"""Pure "extract text from a document" logic — no FastAPI, no HTTP.

Kept independent of api/document_routes.py the same way aiq_agent.ocr's
OcrPipeline is kept independent of api/ocr_routes.py: the interesting
decisions (which extraction path a file takes, when to fall back to OCR)
live here where they're directly unit-testable; the route module is a thin
adapter that reads an UploadFile and turns DocumentExtractionError into an
HTTP response.

Adds extraction paths that had no prior infrastructure anywhere in the
repo (PDF embedded-text-layer, DOCX, XML) and reuses the existing
PaddleOCR/RapidOCR + Qwen3-correction OcrPipeline for everything that
still needs OCR (scanned PDFs, images) — see aiq_agent.ocr.pipeline.

XML note: no Vietnamese e-invoice XML schema parser (VNPT/Viettel/MISA/
BKAV etc. all differ) exists anywhere in this repo, and building one
tied to a specific vendor's tag names would silently mis-handle every
other vendor's export. Instead, XML is flattened schema-agnostically —
every element's tag + text (and any attributes) becomes one readable
"label: value" line — and handed to the SAME downstream LLM-driven
field extraction (skill `voucher-invoice-vat`, capability
`accounting.invoice`) that already turns raw/OCR'd invoice text into
structured fields. This is "reuse the existing structured extraction"
in the only sense that one already exists in this codebase: prompt-
driven, not tag-schema-driven.
"""

from __future__ import annotations

import asyncio
import io
from dataclasses import dataclass
from typing import Protocol

from aiq_agent.ocr.pdf import extract_pdf_text_layer_async, render_pdf_pages_async

_IMAGE_SUFFIXES = (".png", ".jpg", ".jpeg")
_MAX_PDF_PAGES = 100
# Below this many stripped characters across the whole document, the PDF's
# embedded text layer is treated as absent/unreliable (e.g. a scanned PDF
# with a handful of stray text-layer artifacts) and OCR runs instead.
TEXT_LAYER_MIN_CHARS = 40


class DocumentExtractionError(Exception):
    """Raised for anything the caller should treat as a 4xx: unsupported
    format, empty/unreadable file, or an extraction result with no text."""

    def __init__(self, message: str, *, status_code: int = 422) -> None:
        super().__init__(message)
        self.status_code = status_code


@dataclass(frozen=True, slots=True)
class DocumentExtractionResult:
    text: str
    mime_type: str
    extraction_method: str  # pdf_text_layer | pdf_ocr | docx_text | image_ocr | xml_text

    @property
    def char_count(self) -> int:
        return len(self.text)


class OcrPipelineLike(Protocol):
    """Structural subset of aiq_agent.ocr.OcrPipeline this module needs —
    lets tests pass a stub without constructing a real pipeline."""

    async def run_pages(self, page_images: list[bytes]) -> object: ...


def _extract_docx_text(data: bytes) -> str:
    try:
        import docx  # type: ignore[import-not-found]
    except ImportError as exc:  # pragma: no cover — python-docx is a base dep
        raise DocumentExtractionError("python-docx is not installed.", status_code=503) from exc

    try:
        document = docx.Document(io.BytesIO(data))
    except Exception as exc:  # noqa: BLE001 — python-docx raises assorted errors for a bad zip/xml
        raise DocumentExtractionError(f"Không đọc được tệp DOCX: {exc}") from exc

    parts = [p.text for p in document.paragraphs]
    for table in document.tables:
        for row in table.rows:
            parts.extend(cell.text for cell in row.cells)
    return "\n".join(p for p in parts if p.strip())


def _extract_xml_text(data: bytes) -> str:
    """Flatten any well-formed XML into readable "tag: value" lines, in
    document order, namespace prefixes stripped. Schema-agnostic on
    purpose — see the module docstring for why."""

    import xml.etree.ElementTree as ET

    # Untrusted upload (requirement: "uploaded invoice content is
    # untrusted") — reject any DOCTYPE/ENTITY declaration outright rather
    # than trying to parse it. Blocks XXE and billion-laughs-style entity-
    # expansion payloads; no legitimate e-invoice export needs either.
    head = data[:2048]
    if b"<!DOCTYPE" in head or b"<!ENTITY" in head:
        raise DocumentExtractionError("Tệp XML chứa DOCTYPE/ENTITY không được hỗ trợ.", status_code=415)

    try:
        root = ET.fromstring(data)  # noqa: S314 — DOCTYPE/ENTITY already rejected above; expat also never fetches external resources by default
    except ET.ParseError as exc:
        raise DocumentExtractionError(f"Không đọc được tệp XML: {exc}") from exc

    lines: list[str] = []

    def local_name(tag: str) -> str:
        return tag.rsplit("}", 1)[-1] if "}" in tag else tag

    def walk(el: ET.Element) -> None:
        tag = local_name(el.tag)
        text = (el.text or "").strip()
        if text:
            lines.append(f"{tag}: {text}")
        for key, value in el.attrib.items():
            if value.strip():
                lines.append(f"{tag}.{local_name(key)}: {value.strip()}")
        for child in el:
            walk(child)

    walk(root)
    return "\n".join(lines)


async def extract_document_text(
    *,
    filename: str,
    data: bytes,
    pipeline: OcrPipelineLike | None,
) -> DocumentExtractionResult:
    """Dispatch on magic bytes / extension, extract, and validate the result.

    `pipeline` may be None when the caller knows OCR won't be needed (unit
    tests exercising only the DOCX/text-layer paths) — it is only actually
    required on a path that needs it (scanned PDF, image), and a clear
    DocumentExtractionError(503) is raised there if it's missing instead of
    an AttributeError.
    """

    name = filename.lower()

    if name.endswith(".doc") and not name.endswith(".docx"):
        raise DocumentExtractionError(
            "Định dạng .doc cũ chưa được hỗ trợ — vui lòng lưu lại thành .docx hoặc PDF rồi tải lên.",
            status_code=415,
        )

    try:
        if data.startswith(b"%PDF-"):
            mime = "application/pdf"
            text_pages = await extract_pdf_text_layer_async(data, max_pages=_MAX_PDF_PAGES)
            text_layer = "\n\n".join(t.strip() for t in text_pages).strip()
            if len(text_layer) >= TEXT_LAYER_MIN_CHARS:
                text, method = text_layer, "pdf_text_layer"
            else:
                # No usable text layer — almost certainly a scanned PDF.
                if pipeline is None:
                    raise DocumentExtractionError("OCR pipeline not configured.", status_code=503)
                png_pages = await render_pdf_pages_async(data, dpi=200, max_pages=_MAX_PDF_PAGES)
                result = await pipeline.run_pages(png_pages)
                text, method = result.joined_text(), "pdf_ocr"  # type: ignore[attr-defined]
        elif name.endswith(".docx") or data.startswith(b"PK\x03\x04"):
            mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            text = await asyncio.to_thread(_extract_docx_text, data)
            method = "docx_text"
        elif name.endswith(".xml") or data.lstrip()[:5] == b"<?xml":
            mime = "application/xml"
            text = await asyncio.to_thread(_extract_xml_text, data)
            method = "xml_text"
        elif name.endswith(_IMAGE_SUFFIXES) or data.startswith((b"\x89PNG\r\n\x1a\n", b"\xff\xd8\xff")):
            mime = "image/png" if data.startswith(b"\x89PNG") or name.endswith(".png") else "image/jpeg"
            if pipeline is None:
                raise DocumentExtractionError("OCR pipeline not configured.", status_code=503)
            from aiq_agent.ocr.image import normalize_image_to_png_async

            png_bytes = await normalize_image_to_png_async(data)
            result = await pipeline.run_pages([png_bytes])
            text, method = result.joined_text(), "image_ocr"  # type: ignore[attr-defined]
        else:
            raise DocumentExtractionError(
                "Chỉ hỗ trợ PDF, DOCX, PNG, JPG/JPEG, XML.", status_code=415,
            )
    except DocumentExtractionError:
        raise
    except ValueError as exc:
        raise DocumentExtractionError(f"Không đọc được tệp: {exc}") from exc
    except Exception as exc:  # noqa: BLE001 — never leak internal parser errors verbatim
        # Covers a malformed/corrupt file (pdfium/python-docx parse errors)
        # AND a missing optional dependency (extract_pdf_text_layer_async /
        # render_pdf_pages_async raise RuntimeError for "pypdfium2 not
        # installed") — both are "couldn't process this upload" to the
        # caller; a 503 distinction isn't actionable for the browser side
        # either way, and pdfium's own parse-failure exception (PdfiumError)
        # is itself a RuntimeError subclass, so the two aren't reliably
        # distinguishable by type.
        raise DocumentExtractionError("Không trích xuất được nội dung từ tệp này.") from exc

    text = text.strip()
    if not text:
        raise DocumentExtractionError("Không trích xuất được nội dung từ tệp này.")

    return DocumentExtractionResult(text=text, mime_type=mime, extraction_method=method)


__all__ = [
    "DocumentExtractionError",
    "DocumentExtractionResult",
    "extract_document_text",
]
