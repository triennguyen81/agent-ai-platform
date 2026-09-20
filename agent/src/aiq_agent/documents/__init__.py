"""Generic (non-voucher, non-OCR-specific) document text extraction."""

from aiq_agent.documents.extraction import (
    DocumentExtractionError,
    DocumentExtractionResult,
    extract_document_text,
)

__all__ = [
    "DocumentExtractionError",
    "DocumentExtractionResult",
    "extract_document_text",
]
