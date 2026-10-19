"""Image → PNG normalizer for OCR ingestion.

The OCR pipeline consumes PNG bytes per page (see `pipeline.run_pages`).
Uploaded voucher images arrive as JPG/PNG/HEIC/WebP with arbitrary EXIF
rotation. This module normalizes any raster image to a single PNG page so
the same pipeline handles both PDF-rendered pages and standalone photos.
"""

from __future__ import annotations

import io

from aiq_agent.ocr.concurrency import run_ocr_native


def normalize_image_to_png(image_bytes: bytes) -> bytes:
    """Read arbitrary image bytes and return a PNG with EXIF rotation applied.

    Raises RuntimeError if Pillow isn't installed or the bytes aren't a
    recognizable image.
    """

    try:
        from PIL import Image, ImageOps  # type: ignore[import-not-found]
    except ImportError as exc:
        raise RuntimeError(
            "Pillow is not installed. Install ocr extras: `pip install '.[ocr]'`"
        ) from exc

    try:
        image = Image.open(io.BytesIO(image_bytes))
    except Exception as exc:
        raise ValueError(f"Cannot decode image: {exc}") from exc

    image = ImageOps.exif_transpose(image)

    if image.mode not in ("RGB", "L"):
        image = image.convert("RGB")

    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


async def normalize_image_to_png_async(image_bytes: bytes) -> bytes:
    return await run_ocr_native(normalize_image_to_png, image_bytes)
