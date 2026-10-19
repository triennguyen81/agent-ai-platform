-- Document review gate hardening.
--
-- A. vouchers.processing_blockers: blockers OWNED by the extraction
--    subsystem, independent of the voucher-type confirmation (which lives in
--    extracted_fields.voucher_type_classification) and of the document
--    review gate (vouchers.document_review_status). Until now a legacy L2
--    enqueue failure was folded into the type-confirmation flag, so an ADMIN
--    type confirmation erased it. Only the extraction job writes this set;
--    every accounting mutation boundary reads it (DocumentAccountingGate).
-- B. Explicit, safe extraction-failure state (provider/runtime failure after
--    OCR): code + stage + time. No upstream exception text is stored.
-- C. document_processing_runs.error_stage.
--
-- Backfill: only vouchers whose extracted_fields already record the legacy L2
-- enqueue failure (extracted_fields.approval_queue_error, written by the
-- previous phase) get 'legacy_l2_failure'. Nothing else is invented and no
-- existing blocker is cleared.

ALTER TABLE vouchers
    ADD COLUMN IF NOT EXISTS processing_blockers TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    ADD COLUMN IF NOT EXISTS extraction_error_code TEXT,
    ADD COLUMN IF NOT EXISTS extraction_error_stage TEXT,
    ADD COLUMN IF NOT EXISTS extraction_failed_at TIMESTAMPTZ;

DO $$ BEGIN
    ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_processing_blockers
        CHECK (processing_blockers <@ ARRAY['legacy_l2_failure']::TEXT[]);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE vouchers ADD CONSTRAINT chk_vouchers_extraction_error_code CHECK (
        extraction_error_code IS NULL OR extraction_error_code IN (
            'provider_unavailable', 'provider_rate_limited', 'provider_session_limit', 'provider_timeout',
            'agent_execution_failed', 'extraction_output_invalid',
            'processing_agent_unavailable', 'processing_agent_version_unavailable', 'processing_provenance_invalid'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

UPDATE vouchers
SET processing_blockers = array_append(processing_blockers, 'legacy_l2_failure')
WHERE extracted_fields ? 'approval_queue_error'
  AND NOT ('legacy_l2_failure' = ANY(processing_blockers));

ALTER TABLE document_processing_runs ADD COLUMN IF NOT EXISTS error_stage TEXT;
