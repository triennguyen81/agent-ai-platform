"""Generic "give me the text of this document" endpoint for Gateway-mediated
uploads (currently: Contract Upload). Thin HTTP adapter over
aiq_agent.documents.extraction — see that module for the actual dispatch/
extraction logic and its unit tests.

No auth dependency here on purpose, same as /ocr/pdf and /ocr/image
(api/ocr_routes.py) — Gateway is the trust boundary for these binary
extraction endpoints (it resolves tenant/department/capability before ever
calling this), not the agent service. Contrast with /knowledge/extract
(api/knowledge_routes.py), which is a directly browser-adjacent, currently
admin-gated feature this module does not touch.
"""

from __future__ import annotations

from fastapi import APIRouter, File, HTTPException, Request, UploadFile, status
from pydantic import BaseModel

from aiq_agent.documents.extraction import DocumentExtractionError, extract_document_text

router = APIRouter(prefix="/documents", tags=["documents"])

_MAX_BYTES = 25 * 1024 * 1024  # matches Gateway's contract-upload cap


class DocumentExtractResponse(BaseModel):
    text: str
    char_count: int
    mime_type: str
    extraction_method: str  # pdf_text_layer | pdf_ocr | docx_text | image_ocr


@router.post("/extract", response_model=DocumentExtractResponse)
async def extract_document(
    request: Request,
    file: UploadFile = File(...),
) -> DocumentExtractResponse:
    data = await file.read(_MAX_BYTES + 1)
    if not data:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "Empty file upload.")
    if len(data) > _MAX_BYTES:
        raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, "File exceeds the size limit.")

    pipeline = getattr(request.app.state, "ocr_pipeline", None)

    try:
        result = await extract_document_text(filename=file.filename or "", data=data, pipeline=pipeline)
    except DocumentExtractionError as exc:
        raise HTTPException(exc.status_code, str(exc)) from exc

    return DocumentExtractResponse(
        text=result.text,
        char_count=result.char_count,
        mime_type=result.mime_type,
        extraction_method=result.extraction_method,
    )


__all__ = ["router", "DocumentExtractResponse"]
