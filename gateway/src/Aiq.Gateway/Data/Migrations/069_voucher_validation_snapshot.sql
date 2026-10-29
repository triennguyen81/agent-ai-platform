-- Chứng từ flow v2 PR2.x: which voucher state a stored validation result
-- describes. Additive only. Down: Data/Migrations/down/069_voucher_validation_snapshot.down.sql
-- (run by hand).
--
-- Results are stored when the voucher changes (create, PATCH, apply-extraction,
-- extraction job, journal suggestions, type change) and at submit (PR3), never by
-- GET /vouchers/{id}/validation. A stored result is current only while both
-- columns still match the voucher:
--   voucher_updated_at  = vouchers.updated_at at validation time (cheap SQL check);
--   voucher_fingerprint = SHA-256 (hex) of the validator input — voucher row, line
--                         items and journal suggestions, which live in their own
--                         tables and do not bump vouchers.updated_at.
-- NULL (rows stored before 069) = unknown snapshot → treat as stale.
ALTER TABLE voucher_validations
    ADD COLUMN IF NOT EXISTS voucher_updated_at  TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS voucher_fingerprint TEXT;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'voucher_validations_fingerprint_check') THEN
    ALTER TABLE voucher_validations ADD CONSTRAINT voucher_validations_fingerprint_check
      CHECK (voucher_fingerprint IS NULL OR voucher_fingerprint ~ '^[0-9a-f]{64}$');
  END IF;
END $$;
