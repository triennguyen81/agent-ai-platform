-- Sprint 6' A4: contract discount terms để tính expected amount cho payin
-- reconciliation (từ payin-reconciliation-flowchart.md). Không phá cột hiện
-- có; add 4 cột mới với default = 0 / NULL nghĩa là "không có CK".
--
-- - trade_discount_pct: CK thương mại (áp dụng ngay tại HĐ, mặc định 0)
-- - early_payment_discount_pct: % CK khi thanh toán trong window
-- - early_payment_days: window để hưởng CK (NULL = không có CK thanh toán sớm)
-- - volume_rebate_tiers: JSONB tiered rebate
--   Ví dụ: [{"threshold": 1000000000, "rebate_pct": 2},
--          {"threshold": 5000000000, "rebate_pct": 5}]
--   Nghĩa là >1 tỷ hưởng 2%, >5 tỷ hưởng 5%.

ALTER TABLE sim_contracts
    ADD COLUMN IF NOT EXISTS trade_discount_pct numeric(5,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS early_payment_discount_pct numeric(5,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS early_payment_days integer,
    ADD COLUMN IF NOT EXISTS volume_rebate_tiers jsonb NOT NULL DEFAULT '[]'::jsonb;

COMMENT ON COLUMN sim_contracts.trade_discount_pct IS
    'CK thương mại (%) — áp dụng ngay khi phát hành HĐ; đã trừ vào total_amount của voucher.';
COMMENT ON COLUMN sim_contracts.early_payment_discount_pct IS
    'CK thanh toán sớm (%) — áp dụng nếu KH trả trong early_payment_days kể từ ngày HĐ.';
COMMENT ON COLUMN sim_contracts.early_payment_days IS
    'Window ngày để hưởng CK thanh toán sớm; NULL = không có CK thanh toán sớm.';
COMMENT ON COLUMN sim_contracts.volume_rebate_tiers IS
    'JSONB array of {threshold, rebate_pct} bậc thang volume rebate. threshold = doanh số luỹ kế VND.';

-- Sample data cho demo (chỉ update contracts đã có mà chưa có terms).
-- Cover cả sales và distribution vì Việt Đăng có 2 loại (dental sales + distribution).
UPDATE sim_contracts
   SET early_payment_discount_pct = 2,
       early_payment_days = 10
 WHERE early_payment_discount_pct = 0
   AND early_payment_days IS NULL
   AND contract_type IN ('sales', 'distribution', 'ar_sale')
   AND contract_value >= 100000000;

-- Contract lớn thì thêm volume rebate 2 bậc
UPDATE sim_contracts
   SET volume_rebate_tiers = '[
        {"threshold": 500000000, "rebate_pct": 1.5},
        {"threshold": 2000000000, "rebate_pct": 3.0}
      ]'::jsonb
 WHERE contract_value >= 500000000
   AND volume_rebate_tiers = '[]'::jsonb
   AND contract_type IN ('sales', 'distribution', 'ar_sale');
