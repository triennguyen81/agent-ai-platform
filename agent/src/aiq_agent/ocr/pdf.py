"""PDF → PNG page renderer.

Uses pypdfium2 (pure-Python wheel, no Poppler dependency). Lazy-imported so
this module can be imported in environments without the optional `[ocr]`
extras installed (e.g. running unit tests with a stub extractor).
"""

from __future__ import annotations

import asyncio
import io


def render_pdf_pages(pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None) -> list[bytes]:
    """Render every page of a PDF to a list of PNG byte strings."""

    try:
        import pypdfium2 as pdfium  # type: ignore[import-not-found]
    except ImportError as exc:
        raise RuntimeError(
            "pypdfium2 is not installed. Install ocr extras: `pip install '.[ocr]'`"
        ) from exc

    scale = dpi / 72.0
    pdf = pdfium.PdfDocument(pdf_bytes)
    pages: list[bytes] = []
    try:
        if max_pages is not None and len(pdf) > max_pages:
            raise ValueError("PDF page limit exceeded")
        for page in pdf:
            image = page.render(scale=scale).to_pil()
            buffer = io.BytesIO()
            image.save(buffer, format="PNG")
            pages.append(buffer.getvalue())
    finally:
        pdf.close()
    return pages


async def render_pdf_pages_async(pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None) -> list[bytes]:
    return await asyncio.to_thread(render_pdf_pages, pdf_bytes, dpi=dpi, max_pages=max_pages)


def extract_pdf_text_layer(pdf_bytes: bytes, *, max_pages: int | None = None) -> list[str]:
    """Return each page's embedded text layer, or "" for a page with none.

    Sibling to render_pdf_pages — same pypdfium2 document, different API
    surface (text extraction instead of rasterization). Used to skip the
    OCR pipeline entirely for PDFs that already carry a real text layer
    (native "Save as PDF" exports, not scans) — see
    aiq_agent.api.document_routes for the caller that decides, per page,
    whether this text is trustworthy enough to skip OCR.
    """

    try:
        import pypdfium2 as pdfium  # type: ignore[import-not-found]
    except ImportError as exc:
        raise RuntimeError(
            "pypdfium2 is not installed. Install ocr extras: `pip install '.[ocr]'`"
        ) from exc

    pdf = pdfium.PdfDocument(pdf_bytes)
    texts: list[str] = []
    try:
        if max_pages is not None and len(pdf) > max_pages:
            raise ValueError("PDF page limit exceeded")
        for page in pdf:
            textpage = page.get_textpage()
            try:
                texts.append(textpage.get_text_range())
            finally:
                textpage.close()
    finally:
        pdf.close()
    return texts


async def extract_pdf_text_layer_async(pdf_bytes: bytes, *, max_pages: int | None = None) -> list[str]:
    return await asyncio.to_thread(extract_pdf_text_layer, pdf_bytes, max_pages=max_pages)
