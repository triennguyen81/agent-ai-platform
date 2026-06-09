-- Phase 4 performance indexes. Apply on fresh installs via the docker-entrypoint
-- initialization scripts. For an existing database, run this file with psql.

-- Books are filtered by uploader in the ACL check path (BookAccessControl).
CREATE INDEX IF NOT EXISTS idx_books_uploaded_by
    ON books(uploaded_by)
    WHERE uploaded_by IS NOT NULL;

-- Hot in GET /books with status filter.
CREATE INDEX IF NOT EXISTS idx_books_status_created_at
    ON books(status, created_at DESC);

-- Pages are always loaded for a single book in order.
-- The existing UNIQUE (book_id, page_number) already serves WHERE book_id = ?
-- queries, so no extra index needed there.

-- Corrections list page joins by page_id.
CREATE INDEX IF NOT EXISTS idx_corrections_page_id
    ON corrections(page_id);

-- Embeddings backup index — IVFFlat for cosine similarity on the corpus.
-- Lists=100 is a reasonable default for <1M vectors; tune based on actual data.
CREATE INDEX IF NOT EXISTS idx_embeddings_vector_cosine
    ON embeddings
    USING ivfflat (embedding vector_cosine_ops)
    WITH (lists = 100);
