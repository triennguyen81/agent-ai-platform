-- docs/implementation/phase-0/20 — preflight for migration 075 (D4). READ ONLY.
-- Run on a production CLONE before migrating:
--   psql "$CLONE_URL" -v ON_ERROR_STOP=1 -f 20_duplicate_preflight.sql
-- The normalisation is written inline (075's functions do not exist yet) and
-- must stay identical to 075 / InvoiceIdentity.
BEGIN TRANSACTION READ ONLY;

-- 1. Groups that would collide on the new unique key: 075 STOPS if any. Review each
--    by hand (same seller, series, number and date after normalisation).
WITH k AS (
    SELECT id, tenant_id, department_id, status, created_at, seller_mst, document_series, document_number, document_date,
           CASE WHEN length(regexp_replace(COALESCE(seller_mst, ''), '[^0-9]', '', 'g')) IN (10, 13)
                     AND NULLIF(upper(regexp_replace(COALESCE(document_series, ''), '[ \t\n\r\f\v]', '', 'g')), '') IS NOT NULL
                     AND NULLIF(ltrim(upper(regexp_replace(COALESCE(document_number, ''), '[ \t\n\r\f\v]', '', 'g')), '0'), '') IS NOT NULL
                     AND document_date IS NOT NULL
                THEN regexp_replace(seller_mst, '[^0-9]', '', 'g') || '|'
                     || upper(regexp_replace(document_series, '[ \t\n\r\f\v]', '', 'g')) || '|'
                     || ltrim(upper(regexp_replace(document_number, '[ \t\n\r\f\v]', '', 'g')), '0') || '|' || to_char(document_date, 'YYYYMMDD')
           END AS dedupe_key
      FROM vouchers
     WHERE tenant_id IS NOT NULL AND status <> 'rejected' AND deleted_at IS NULL)
SELECT tenant_id, dedupe_key, COUNT(*) AS vouchers,
       array_agg(id ORDER BY created_at) AS voucher_ids, array_agg(DISTINCT department_id) AS departments,
       array_agg(status ORDER BY created_at) AS statuses
  FROM k WHERE dedupe_key IS NOT NULL
 GROUP BY tenant_id, dedupe_key HAVING COUNT(*) > 1
 ORDER BY tenant_id, dedupe_key;

-- 2. How many vouchers get a key at all (the others have no series / no valid MST:
--    the database no longer guards them; the job classifies them as possible duplicates).
SELECT COUNT(*) FILTER (WHERE seller_mst IS NOT NULL AND document_number IS NOT NULL AND document_date IS NOT NULL) AS had_old_key,
       COUNT(*) FILTER (WHERE document_series IS NULL OR btrim(document_series) = '') AS without_series,
       COUNT(*) AS vouchers
  FROM vouchers WHERE tenant_id IS NOT NULL AND deleted_at IS NULL;

-- 3. Size of the table (the ADD COLUMN ... STORED rewrite and the index build lock writes).
SELECT pg_size_pretty(pg_total_relation_size('vouchers')) AS vouchers_total_size, (SELECT COUNT(*) FROM vouchers) AS rows;

ROLLBACK;
