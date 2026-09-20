# Knowledge Base / RAG implementation

## Before

- `notes` was the canonical personal, department and platform knowledge store.
- `agent_knowledge` attached individual notes to agents. Gateway manifest resolution already enforced agent execution authorization.
- `EmbeddingService` lazily embedded whole notes into the existing `aiq_notes` Qdrant collection, scoped by tenant/user.
- Dynamic executors placed complete attached note contents in system prompts. There were no document versions, deterministic chunks, indexing jobs, active-version filtering or durable vector cleanup.
- The operational Knowledge screen was `/department/knowledge`; `/knowledge` redirects there.

## After

```text
Existing Knowledge UI / Notes APIs
  → knowledge_bases
  → notes (canonical document rows)
  → immutable document_versions
  → indexing_jobs
  → normalized document_chunks + embedding metadata
  → existing FastEmbed model / existing Qdrant collection
  → authorized, active-version retrieval
  → bounded untrusted excerpts and source metadata
  → existing provider executor / agent response
```

There is no second document-content system. `notes` remains the document table. Existing notes and `agent_knowledge` attachments remain valid. Embeddings remain in Qdrant; model/dimension/index state are recorded with the Postgres chunks.

## Migration

`gateway/src/Aiq.Gateway/Data/Migrations/047_knowledge_rag.sql` is picked up by the existing Gateway migration runner.

- Adds `knowledge_bases`, `document_versions`, `document_chunks`, `agent_knowledge_bases` and `indexing_jobs`.
- Adds KB, MIME, source, active-version and verification fields to `notes`.
- Creates **one compatibility KB per existing note** and queues its initial version. This avoids accidentally granting an agent every note in a department or tenant.
- Keeps legacy attachment authorization in `agent_knowledge`; deleting a legacy attachment still revokes access immediately.
- Triggers version and queue all existing note create/update paths. Ownership and version contents are immutable; versions and chunks remain available for audit after a document is deleted.
- A changed upload with the same filename in a KB updates the same document and creates a version. Reuploading identical text/title does not create another version. Explicit document selection supports replacement with a different filename.
- Composite KB/tenant foreign keys reject cross-tenant explicit attachments. Gateway also checks structural department ownership of the agent.

Apply through the normal migration runner, then start the updated runtime with database access. The worker indexes the queued compatibility versions. Retrieval fails closed during outages; it never falls back to full-document prompts.

## Ingestion and lifecycle

Department-admin endpoints live under `/department/knowledge-bases`. Tenant, department and actor are obtained from the authenticated Gateway principal.

Uploads accept UTF-8 text/Markdown plus DOCX/PDF/PNG/JPEG. DOCX reuses the existing document text extractor without OCR or LLM calls. Binary extraction reuses the existing PDF renderer and OCR adapter through an authenticated runtime endpoint; it does not use the LLM correction stage. OCR pages are separated by form-feed before chunking. Upload limit: 10 MB, 100 PDF pages, 2 million extracted characters. Original binary uploads are not retained by this feature; immutable extracted text is retained.

The worker normalizes Unicode and line endings per page, creates overlapping bounded chunks, and preserves normalized offsets, page and Markdown section. IDs are UUIDv5 derived from version, chunk configuration and location. Sections are capped to avoid unbounded metadata in model context.

Indexing holds database job/document locks, uses `SKIP LOCKED` to coordinate workers, and performs idempotent vector upserts with `wait=True`. The active version changes only after all new chunks have been indexed. Failed replacement indexing leaves the previous active version in place. A crash rolls back activation and leaves a retryable job.

Activation, archive, deletion and KB deactivation enqueue durable cleanup jobs. Cleanup removes superseded/disabled/orphaned document vectors and legacy whole-note vectors. Failed indexing retries up to five times with a one-minute minimum delay; manual reindex creates a new job. Cleanup retries continue after outages. Errors and retry state remain in Postgres, logs, and document status responses.

A rotating reconciliation pass checks up to 20 active documents each minute, with a five-minute verification interval per document, and queues repair when expected points or scope payloads are missing. Retrieval remains gated by Postgres during cleanup/repair delays.

## Retrieval, manifest and isolation

Gateway sends `knowledgeBaseIds` and compatibility note references; manifest note `content` is empty. Browser-provided knowledge is not used by the dynamic-agent execution path.

At execution, Python:

1. Uses the Gateway manifest's tenant, department and agent identity.
2. Rechecks explicit KB attachments and legacy note attachments in Postgres.
3. Resolves active, indexed document versions.
4. Embeds the query and searches Qdrant with mandatory tenant/KB filters and the authorized active-version IDs.
5. Rechecks returned chunk identities, ownership, attachments, active version and archive status in Postgres.
6. Returns bounded chunks with document ID/title, version ID/number, page, section, offsets and score.

Explicitly attached platform knowledge retains its existing shared semantics: it uses a reserved platform namespace and an explicit authorized KB allowlist. This is separate from tenant namespaces; no query searches all tenants or all platform documents.

No-hit results explicitly tell the executor to state that it lacks sufficient KB evidence. The system policy requires citations by document, page/section and version. Existing Markdown answer rendering displays those citations.

## Security and business data

Document text is serialized as untrusted JSON evidence in the user/data message, never concatenated into the system authority prompt. Tool grants, tenant identity, delegation and HITL remain separate manifest/Gateway controls. Claude CLI built-in tools are explicitly disabled; only the granted MCP tool set is registered. Existing granted-tool HITL wrappers remain in place. Agent-scoped `search_notes` and `get_note` handlers are also routed through the authorized chunk retriever; they cannot use the old personal Notes API to bypass KB attachment scope or return full documents.

This prevents document text from changing authorization or enabling arbitrary shell/filesystem tools. Prompt wording alone is not a proof that an LLM will never follow malicious text; tests verify the enforceable boundaries and data/authority separation, not universal model immunity.

KBs hold policies and reference documents. System guidance and the upload UI direct live balances, accounting transactions and AR/AP facts to existing business tools/Postgres. Mixed questions combine authorized tool results with retrieved policy. There is no automated semantic classifier preventing an administrator from uploading transactional text.

## UI

The existing Knowledge page uses one KB-scoped “Thêm tài liệu” form with upload and direct-entry tabs. Both save through the same document/version/indexing pipeline. The duplicate lower Notes form/list has been removed; legacy APIs remain compatible. Documents and versions appear together, followed by KB-to-Agent attachment controls. Reindex/retry, disable/restore/delete and KB deactivate/delete remain available. Status refreshes every five seconds. See `KNOWLEDGE_UI_SIMPLIFICATION.md` for the UI change and its validation.

New management routes preserve department ownership. The existing platform Knowledge UI and legacy attachment controls continue to operate through the migration triggers; new multi-document KB creation is currently a department-admin operation.

## Configuration

- `KNOWLEDGE_POSTGRES_DSN`: runtime Postgres access; falls back to `MEMORY_POSTGRES_DSN` outside Compose. Compose defaults to its existing Postgres service. URL-encode special password characters when setting a DSN.
- `KNOWLEDGE_CHUNK_SIZE=800`, `KNOWLEDGE_CHUNK_OVERLAP=120` (characters).
- `KNOWLEDGE_SCORE_THRESHOLD=0.25`, `KNOWLEDGE_CONTEXT_CHARS=12000`.
- Existing `EMBEDDING_*`, `QDRANT_*`, and `QDRANT_TOP_K` settings remain authoritative.

Changing embedding model/dimension requires operational reindexing and a compatible collection, as with the existing embedding service. Production relevance thresholds and chunk sizes should be evaluated against representative Vietnamese policy queries.

## Files changed for this task

- Gateway: migration `047_knowledge_rag.sql`; `Notes/KnowledgeBaseRepository.cs`; `Endpoints/KnowledgeBaseEndpoints.cs`; manifest enrichment in `Endpoints/DynamicAgentEndpoints.cs`; reference-only manifest queries/contract in `Governance/IDynamicAgentRepository.cs`; service/route registration in `Program.cs`.
- Python: new `knowledge/{chunking,service,__init__}.py`; new `api/knowledge_routes.py`; extended `embeddings/service.py`; `runtime/{manifest,executors}.py`; `main.py`, `config.py`, and the page limit in `ocr/pdf.py`.
- Web: `components/knowledge-bases.tsx`; existing department Knowledge page; authenticated `/api/knowledge-bases/[[...path]]` proxy and tests.
- Configuration: `.env.example`, `docker-compose.yml`.
- Tests: Python knowledge/unit/integration suites; Gateway `KnowledgeBaseIntegrationTests.cs`; three older Gateway fixtures updated with the already-existing `must_change_password` field so the full real-Postgres suite can run.

Pre-existing unrelated workspace changes were preserved.

## Validation

Final results:

| Check | Result |
| --- | --- |
| Gateway tests, real Postgres enabled | 263 passed |
| Python tests, real Postgres/Qdrant and production encoder enabled | 126 passed |
| Web tests | 35 passed |
| TypeScript `tsc --noEmit` | Passed |
| Next.js production build | Passed |
| Gateway build (part of `dotnet test`) | Passed |
| `docker compose config --quiet` | Passed |
| `git diff --check` | Passed |

Real-service coverage includes existing-note migration, KB creation/attachment, actual FastEmbed generation, scoped Qdrant retrieval, cross-tenant/unattached rejection, immutable version replacement, disable/delete, reindex, failure/retry, partial-write cleanup, and missing-vector reconciliation. Python also verifies data/authority separation, no built-in CLI tools, scoped legacy note tools, OCR adapter reuse, page provenance, and extraction authorization.

Gateway tests ran with `AIQ_TEST_POSTGRES`; Python tests ran with `TEST_KNOWLEDGE_DSN`, `TEST_QDRANT_URL`, and `TEST_REAL_EMBEDDING=1`. Tests use disposable schemas and uniquely named collections and clean them up; application data and deployed services were not migrated or replaced. Remaining test warnings are pre-existing nullable fixture warnings and library notices about local HTTP/API-key transport and FastEmbed pooling.

The real-service E2E covers extraction-independent ingestion, embedding, Postgres lifecycle, Qdrant retrieval and isolation. Provider execution is tested using stub LLM clients. A live browser-to-provider answer and OCR accuracy on a representative business-document corpus are not certified by these tests.

Implementation references: [Qdrant filtering](https://qdrant.tech/documentation/search/filtering/) and [synchronous upsert acknowledgement](https://api.qdrant.tech/api-reference/points/upsert-points).
