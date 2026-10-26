"""Authenticated document extraction; uses the existing OCR adapters, no LLM."""

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile

from aiq_agent.api.security import RuntimePrincipal, require_principal
from aiq_agent.documents.extraction import DocumentExtractionError, extract_document_text
from aiq_agent.ocr.concurrency import OcrBusyError, OcrTimeoutError, run_ocr_native
from aiq_agent.ocr.pdf import render_pdf_pages

router = APIRouter()


@router.post("/knowledge/extract")
async def extract_knowledge(
    request: Request,
    file: UploadFile = File(...),
    principal: RuntimePrincipal = Depends(require_principal),
):
    if "admin" not in principal.roles or not principal.tenant_id or not principal.department_id:
        raise HTTPException(403, "Department administrator required")
    data = await file.read(10 * 1024 * 1024 + 1)
    if not data or len(data) > 10 * 1024 * 1024:
        raise HTTPException(413, "Document size must be 1 byte to 10 MB")
    try:
        if (file.filename or "").lower().endswith(".docx"):
            # Reuse the canonical document extractor; DOCX needs no OCR or LLM.
            document = await extract_document_text(filename=file.filename, data=data, pipeline=None)
            if len(document.text)>2_000_000 or "\0" in document.text:
                raise HTTPException(422, "Document too large or invalid text")
            return {"text": document.text, "mimeType": document.mime_type}
        if data.startswith(b"%PDF-"):
            pages = await run_ocr_native(render_pdf_pages, data, dpi=150, max_pages=100)
            mime = "application/pdf"
        elif data.startswith(b"\x89PNG\r\n\x1a\n") or data.startswith(b"\xff\xd8\xff"):
            pages = [data]
            mime = "image/png" if data.startswith(b"\x89PNG") else "image/jpeg"
        else:
            raise HTTPException(415, "Supported binary documents: PDF, DOCX, PNG, JPEG")
        extracted = await request.app.state.ocr_extractor.extract(pages)
        text = "\f".join(page.text for page in extracted)
        if not text.strip() or len(text) > 2_000_000:
            raise HTTPException(422, "No extractable text or document too large")
        return {"text": text, "mimeType": mime}
    except (OcrBusyError, OcrTimeoutError) as exc:
        raise HTTPException(exc.status_code, str(exc)) from exc
    except DocumentExtractionError as exc:
        raise HTTPException(exc.status_code, "Document extraction failed") from exc
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(422, "Document extraction failed") from exc
