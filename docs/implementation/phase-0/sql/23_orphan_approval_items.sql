-- docs/implementation/phase-0/23 §6 (B-1) — READ ONLY. Approval-queue items whose voucher has
-- no department (or no longer exists): after 23 nobody can list, read or close them.
-- Run on a production CLONE:  psql "$CLONE_URL" -v ON_ERROR_STOP=1 -f 23_orphan_approval_items.sql
BEGIN TRANSACTION READ ONLY;
SELECT q.tenant_id, q.status, COUNT(*) AS items, MIN(q.created_at) AS oldest, MAX(q.created_at) AS newest,
       COUNT(*) FILTER (WHERE v.id IS NULL) AS voucher_missing
  FROM sim_approval_queue q LEFT JOIN vouchers v ON v.id = q.voucher_id
 WHERE v.id IS NULL OR v.department_id IS NULL
 GROUP BY q.tenant_id, q.status
 ORDER BY q.tenant_id, q.status;
ROLLBACK;
