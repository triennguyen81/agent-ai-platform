# Unified Knowledge document entry — 2026-09-20

## Why there were two add paths

The department Knowledge server page still rendered its original Notes create action (`createDepartmentKnowledge`) and a separate Notes list. The newer `KnowledgeBases` client panel had been inserted above it, bringing its own file-upload form and version list.

The storage was already canonical: migration 047 extended `notes` into document records and added version/index-job triggers. However, the old manual form did not carry a selected KB, so its writes created a per-note compatibility KB. The two UI paths therefore had different destinations despite sharing storage.

## Canonical path after the fix

```text
Select KB → + Thêm tài liệu
  ├─ Tải tệp lên: PDF / DOCX / TXT / MD / PNG / JPG
  └─ Nhập trực tiếp: title / tags / content
       → POST /department/knowledge-bases/{kbId}/documents
       → KnowledgeBaseRepository.SaveDocumentAsync
       → notes in the selected KB
       → existing immutable versions / indexing jobs
       → existing chunks / embeddings / Qdrant
```

The existing authenticated web proxy forwards both multipart uploads and JSON direct entry to the same endpoint. Gateway derives tenant, department and actor from the principal and verifies KB ownership. Direct-entry requests are validated for required title/content, size, tags and invalid text. File uploads keep their existing size/type validation. Both paths can target an existing document ID to create a new version; document updates must belong to the selected KB and ownership scope.

DOCX uses the existing `documents.extraction.extract_document_text` implementation, with no OCR/LLM for DOCX. Existing PDF/image OCR and TXT/MD paths remain unchanged. No new extraction library, document store, chunker or indexing system was introduced.

## UI behavior

- One “Thêm tài liệu” button, disabled until a KB is selected and loaded.
- “Đang thêm vào KB: <name>” explicitly identifies the destination.
- One expandable document form with two accessible tabs using the existing Tabs/Input/Button styling.
- One document list; versions are grouped under each document, with actions shown once per document.
- Tags are displayed on the document and preserved when a file replaces manually entered content.
- KB-to-Agent attachment controls remain below the document list.
- The lower legacy form, actions and duplicate list were removed from the department page.
- Requests for an old selection are cancelled, preventing stale documents/version choices from appearing under another selected KB.

## Backward compatibility

No schema migration or data relocation is required. Existing migration 047 already assigned legacy notes to compatibility KBs; those documents are accessible through their corresponding KB in the selector. The legacy Notes/department Knowledge APIs and agent-note attachments remain intact for existing callers and Agent Builder behavior. Legacy note creation still creates a compatibility KB and queues indexing through the same triggers.

New manual entry saves into the chosen KB with `source='manual'`. Existing upload callers keep their optional-argument defaults and same-filename behavior. Updating content preserves document identity and immutable version history. Tags live on the canonical document record; uploads do not erase them.

## Files changed in this task

| Area | Files |
| --- | --- |
| Page and UI | `web/src/app/(app)/department/knowledge/page.tsx`, `web/src/components/knowledge-bases.tsx`, new `web/src/components/knowledge-document-form.tsx` |
| Web tests | new `web/src/components/knowledge-document-form.test.ts`, `web/src/app/api/knowledge-bases/[[...path]]/route.test.ts`, `web/vitest.config.ts` (automatic JSX for SSR component tests) |
| Gateway | `gateway/src/Aiq.Gateway/Endpoints/KnowledgeBaseEndpoints.cs`, `gateway/src/Aiq.Gateway/Notes/KnowledgeBaseRepository.cs` |
| Gateway tests | `gateway/tests/Aiq.Gateway.Tests/Data/KnowledgeBaseIntegrationTests.cs` |
| Python | `agent/src/aiq_agent/api/knowledge_routes.py`, `agent/tests/test_knowledge.py` |
| Documentation | this report and the updated UI/upload descriptions in `docs/KNOWLEDGE_RAG_IMPLEMENTATION.md` |

Unrelated existing workspace changes were preserved.

## Verification

| Check | Result |
| --- | --- |
| Web `npm test -- --reporter=dot` | 48 passed |
| Web `npx tsc --noEmit` | Passed |
| Web `npm run build` | Passed |
| Gateway `dotnet test ... --filter FullyQualifiedName~Knowledge` with real Postgres | 17 passed; Gateway compiled successfully |
| Python `test_knowledge.py`, `test_knowledge_integration.py`, `test_document_extraction.py` with real Postgres/Qdrant | 37 passed, 1 skipped |
| `git diff --check` | Passed |

The optional production-encoder download test was skipped; this change does not modify embedding generation. Real Postgres/Qdrant lifecycle tests ran with deterministic vectors. DOCX tests parsed real DOCX paragraphs/tables and rejected corrupt files. Gateway tests verified manual content/tags, no implicit extra KB, version/index-job creation, upload replacement, foreign tenant/department/KB rejection and legacy Notes compatibility. Web tests verified the initial disabled state, absence of the old lower form, both tab modes, supported extensions, and both request formats using the same scoped endpoint.

No application database migration or running-service deployment was performed by this task. These checks do not include an interactive browser acceptance test.
