"""PDF → PNG page renderer.

Uses pypdfium2 (pure-Python wheel, no Poppler dependency). Lazy-imported so
this module can be imported in environments without the optional `[ocr]`
extras installed (e.g. running unit tests with a stub extractor).
"""

from __future__ import annotations

import asyncio
import io


def render_pdf_pages(pdf_bytes: bytes, *, dpi: int = 200) -> list[bytes]:
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
        for page in pdf:
            image = page.render(scale=scale).to_pil()
            buffer = io.BytesIO()
            image.save(buffer, format="PNG")
            pages.append(buffer.getvalue())
    finally:
        pdf.close()
    return pages


async def render_pdf_pages_async(pdf_bytes: bytes, *, dpi: int = 200) -> list[bytes]:
    return await asyncio.to_thread(render_pdf_pages, pdf_bytes, dpi=dpi)
