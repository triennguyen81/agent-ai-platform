"""PDF → PNG page renderer.

Uses pypdfium2 (pure-Python wheel, no Poppler dependency). Lazy-imported so
this module can be imported in environments without the optional `[ocr]`
extras installed (e.g. running unit tests with a stub extractor).
"""

from __future__ import annotations

import io

from aiq_agent.ocr.concurrency import PDFIUM_LOCK, run_ocr_native


def render_pdf_pages(
    pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None
) -> list[bytes]:
    """Render every page of a PDF to a list of PNG byte strings.

    Holds PDFIUM_LOCK for the whole document: PDFium is not thread-safe.
    """

    try:
        import pypdfium2 as pdfium  # type: ignore[import-not-found]
    except ImportError as exc:
        raise RuntimeError(
            "pypdfium2 is not installed. Install ocr extras: `pip install '.[ocr]'`"
        ) from exc

    scale = dpi / 72.0
    pages: list[bytes] = []
    with PDFIUM_LOCK:
        pdf = pdfium.PdfDocument(pdf_bytes)
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


async def render_pdf_pages_async(
    pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None
) -> list[bytes]:
    return await run_ocr_native(render_pdf_pages, pdf_bytes, dpi=dpi, max_pages=max_pages)


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

    texts: list[str] = []
    with PDFIUM_LOCK:
        pdf = pdfium.PdfDocument(pdf_bytes)
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


async def extract_pdf_text_layer_async(
    pdf_bytes: bytes, *, max_pages: int | None = None
) -> list[str]:
    return await run_ocr_native(extract_pdf_text_layer, pdf_bytes, max_pages=max_pages)


# ---- Text-layer-first page analysis (OCR performance audit) ---------------

# A page whose images cover at least this share of its area is treated as a
# scan and always OCR'd, whatever its text layer says (a scan can carry a
# small digital header/stamp text while the real content is in the image).
SCAN_IMAGE_COVERAGE = 0.5
MIN_PAGE_TEXT_CHARS = 40


def usable_text_layer(text: str) -> bool:
    """True when a page's embedded text is long and clean enough to be used
    verbatim instead of OCR (no replacement / control / private-use
    characters, mostly letters and digits)."""
    import unicodedata

    stripped = text.strip()
    if len(stripped) < MIN_PAGE_TEXT_CHARS:
        return False
    visible = [ch for ch in stripped if not ch.isspace()]
    bad = sum(1 for ch in visible if ch == "�" or unicodedata.category(ch) in ("Cc", "Co", "Cn"))
    alnum = sum(1 for ch in visible if ch.isalnum())
    return bad / len(visible) < 0.01 and alnum / len(visible) >= 0.5


def analyse_pdf_pages(
    pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None
) -> list[dict]:
    """One PDFium pass: per page, its text layer, image coverage, and — only
    for pages that need OCR — a rendered PNG.

    Returns [{"text": str, "image_coverage": float, "use_text_layer": bool,
    "png": bytes | None}] in page order. Holds PDFIUM_LOCK for the whole
    document, like the other PDFium helpers.
    """

    try:
        import pypdfium2 as pdfium  # type: ignore[import-not-found]
        import pypdfium2.raw as pdfium_c  # type: ignore[import-not-found]
    except ImportError as exc:
        raise RuntimeError(
            "pypdfium2 is not installed. Install ocr extras: `pip install '.[ocr]'`"
        ) from exc

    scale = dpi / 72.0
    out: list[dict] = []
    with PDFIUM_LOCK:
        pdf = pdfium.PdfDocument(pdf_bytes)
        try:
            if max_pages is not None and len(pdf) > max_pages:
                raise ValueError("PDF page limit exceeded")
            for page in pdf:
                textpage = page.get_textpage()
                try:
                    text = textpage.get_text_range()
                finally:
                    textpage.close()
                width, height = page.get_size()
                covered = 0.0
                for obj in page.get_objects(filter=(pdfium_c.FPDF_PAGEOBJ_IMAGE,), max_depth=3):
                    left, bottom, right, top = obj.get_bounds()
                    covered += max(right - left, 0) * max(top - bottom, 0)
                coverage = min(covered / (width * height), 1.0) if width and height else 0.0
                use_text = coverage < SCAN_IMAGE_COVERAGE and usable_text_layer(text)
                png = None
                if not use_text:
                    image = page.render(scale=scale).to_pil()
                    buffer = io.BytesIO()
                    image.save(buffer, format="PNG")
                    png = buffer.getvalue()
                out.append(
                    {
                        "text": text,
                        "image_coverage": coverage,
                        "use_text_layer": use_text,
                        "png": png,
                    }
                )
        finally:
            pdf.close()
    return out


async def analyse_pdf_pages_async(
    pdf_bytes: bytes, *, dpi: int = 200, max_pages: int | None = None
) -> list[dict]:
    return await run_ocr_native(analyse_pdf_pages, pdf_bytes, dpi=dpi, max_pages=max_pages)
