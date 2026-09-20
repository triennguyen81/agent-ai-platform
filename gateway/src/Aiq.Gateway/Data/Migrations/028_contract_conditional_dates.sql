-- Contract Processing follow-up: conditional/triggered date semantics.
--
-- Root cause this fixes: payment installments and milestones whose real
-- due date is not yet knowable (it depends on a future event — a signed
-- delivery record, a valid VAT invoice, etc.) were only representable as
-- either a fixed due_date or NULL, with no way to say *why* it's null or
-- to record a non-binding planning estimate. That made it impossible to
-- distinguish "no date extracted" from "date is genuinely conditional",
-- and risked a caller writing an estimated date into due_date as if it
-- were a confirmed legal deadline.
--
-- trigger_event   — human-readable description of the event this date
--                    waits on (e.g. "Ký Biên bản giao nhận + hóa đơn GTGT
--                    hợp lệ"). NULL means the date is not conditional.
-- offset_days     — business/calendar days after trigger_event occurs
--                    (payment schedules only).
-- duration_months — duration after trigger_event occurs (milestones only,
--                    e.g. warranty period).
-- expected_date   — a non-binding planning estimate, only ever populated
--                    alongside a trigger_event (never a substitute for
--                    due_date once the trigger actually resolves).
ALTER TABLE contract_payment_schedules
    ADD COLUMN IF NOT EXISTS trigger_event TEXT,
    ADD COLUMN IF NOT EXISTS offset_days INT,
    ADD COLUMN IF NOT EXISTS expected_date DATE;

ALTER TABLE contract_milestones
    ADD COLUMN IF NOT EXISTS trigger_event TEXT,
    ADD COLUMN IF NOT EXISTS duration_months INT,
    ADD COLUMN IF NOT EXISTS expected_date DATE;
