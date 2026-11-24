# AIQ — Kết quả kiểm thử & độ phủ test thuế

Ngày chạy: 2026-11-13 · Commit `b0d7aaf`. Không sửa mã nguồn, không chạy migration, không ghi DB.

## 1. Tổng hợp

| Nhóm | Tổng | PASS | FAIL | SKIPPED | NOT VERIFIED |
|---|---|---|---|---|---|
| Probe golden-case engine thuế (mới, đọc-only) | 26 | 12 | **14** | 0 | 0 |
| Agent pytest liên quan (sẵn có) | 34 | 34 | 0 | 0 | 0 |
| Gateway unit test liên quan (sẵn có, không DB) | 125 | 125 | 0 | 0 | 0 |
| Gateway integration test có DB (sẵn có) | — | — | — | **toàn bộ** | — |
| E2E / live trình duyệt | — | — | — | — | **toàn bộ** |
| **Cộng (đã chạy)** | **185** | **171** | **14** | | |

Test **có sẵn** cho `calculate_vat`, `calculate_pit`, `calculate_cit`, `check_filing_deadline`: **0** (grep `agent/tests`, `tests/`). Các test sẵn có đều xanh vì chúng không chạm vào phần tính thuế — không được coi là bằng chứng đúng nghiệp vụ thuế.

## 2. Probe golden-case

- Script: [evidence/tax_probe.py](evidence/tax_probe.py). Gọi trực tiếp handler của tool (không LLM, không mạng, không DB), so với giá trị kỳ vọng tính tay theo luật của kỳ được kiểm.
- Chạy: `docker exec -i aiq-agent python -I - < docs/audit/evidence/tax_probe.py`.
- Bản trong container khác bản repo **chỉ ở định dạng xuống dòng** (đã `diff`), logic giống hệt.

| Mã | KQ | Ca kiểm | Thực tế | Kỳ vọng |
|---|---|---|---|---|
| VAT-01 | PASS | 110tr gồm VAT 10% → VAT | 10.000.000 | 10.000.000 |
| VAT-02 | PASS | 108tr gồm VAT 8% → VAT | 8.000.000 | 8.000.000 |
| VAT-03 | PASS | 100tr chưa VAT 5% → VAT | 5.000.000 | 5.000.000 |
| VAT-04 | **FAIL** | 1.234.567 gồm 10% → VAT làm tròn đồng | 112.233,36 | 112.233 |
| VAT-05 | PASS | thuế suất 7% bị từ chối | từ chối | từ chối |
| VAT-06 | PASS | 0% → VAT 0 | 0 | 0 |
| PIT-01 | **FAIL** | Giảm trừ bản thân kỳ 2026 | 11.000.000 | 15.500.000 |
| PIT-02 | **FAIL** | Gross 30tr, 0 NPT, kỳ 2026 | 1.627.500 | 635.000 |
| PIT-03 | **FAIL** | Giảm trừ 2 NPT kỳ 2026 | 8.800.000 | 12.400.000 |
| PIT-04 | **FAIL** | BH NLĐ gross 50tr (trần BHTN 20× LTTV vùng I) | 4.914.000 | 4.946.000 |
| PIT-05 | **FAIL** | Gross 50tr, 2 NPT, kỳ 2026 | 3.407.200 | 1.215.400 |
| PIT-06 | PASS | Biểu 7 bậc: TNTT 25tr (đúng cho kỳ 2025) | 3.350.000 | 3.350.000 |
| PIT-07 | **FAIL** | TNTT đúng 10tr (biên bậc 2026) | 1.425.000 | 500.000 |
| CIT-01 | **FAIL** | DT 10 tỷ, không truyền rate → 17% | 400.000.000 | 340.000.000 |
| CIT-02 | **FAIL** | DT 2 tỷ, không truyền rate → 15% | 100.000.000 | 75.000.000 |
| CIT-03 | **FAIL** | DT 200 tỷ + rate 17% phải bị từ chối | chấp nhận | từ chối |
| CIT-04 | PASS | DT 100 tỷ, lỗ 5 tỷ, 20% | 3.000.000.000 | 3.000.000.000 |
| CIT-05 | PASS | Lỗ âm bị bỏ qua | 4.000.000.000 | 4.000.000.000 |
| DL-01 | PASS | 01/GTGT T12/2026 | 20/01/2027 | 20/01/2027 |
| DL-02 | PASS | 01/GTGT Q1/2026 (chưa dời ngày nghỉ) | 30/04/2026 | 30/04/2026 |
| DL-03 | **FAIL** | 01/GTGT Q2/2026 | 30/07/2026 | 31/07/2026 |
| DL-04 | **FAIL** | 01/GTGT Q3/2026 | 30/10/2026 | 31/10/2026 |
| DL-05 | **FAIL** | 01/GTGT Q4/2026 | 30/01/2027 | 31/01/2027 |
| DL-06 | **FAIL** | 02/QTT-TNCN năm 2025 | 31/03/2026 | 30/04/2026 |
| DL-07 | PASS | 05/QTT-TNCN năm 2025 | 31/03/2026 | 31/03/2026 |
| DL-08 | PASS | 03/TNDN năm 2025 (năm dương lịch) | 31/03/2026 | 31/03/2026 |

Ghi chú kỳ vọng: PIT kỳ 2026 dùng giảm trừ NQ 110/2025 và biểu 5 bậc Luật 109/2025 (**LRR** ngày áp dụng biểu); nếu chuyên gia kết luận biểu 5 bậc chỉ áp dụng từ 01/07/2026 thì PIT-02/05/07 vẫn FAIL vì giảm trừ sai. DL-02 chưa tính dời ngày nghỉ (30/04 → 04/05/2026), nên PASS ở đây chỉ là PASS theo công thức.

## 3. Test sẵn có đã chạy

**Agent** (image `aiq-agent-pytest`, mount repo read-only):
`tests/test_tax_verification_tools.py`, `test_expense_payment_tools.py`, `test_save_voucher_conflicts.py`, `test_runtime_security.py`, `test_registry.py` → **34 passed**.

**Gateway** (`mcr.microsoft.com/dotnet/sdk:9.0`, filter):
`VoucherValidatorTests`, `ExpenseClaimItemRulesTests`, `JournalRoutingRulesTests`, `InvoiceDirectionResolverTests`, `AccountingPeriodTests` → **125 passed, 0 failed, 0 skipped**.
`git status` sau khi chạy: không thay đổi file theo dõi.

## 4. Không chạy (SKIPPED) và lý do

| Test | Lý do |
|---|---|
| `TaxVerificationIntegrationTests`, `VoucherValidationIntegrationTests`, `ExpenseClaim*IntegrationTests`, `AccountingPeriod*` (DB), `FreshDatabaseMigratorTests` | Tạo database tạm và chạy migration trên Postgres — nằm ngoài phạm vi "không thay đổi database" của audit. Có thể chạy ở giai đoạn sau khi được phép. |
| E2E qua trình duyệt / gọi agent thật | Cần đổi mật khẩu tạm và tạo dữ liệu LIVE-TEST — cần phê duyệt riêng. |
| Integration MISA→AIQ, Payroll→PIT, GL→CIT, Tax Engine→Declaration | **Không có hệ thống để test** (NOT IMPLEMENTED). |

## 5. Bộ test cần bổ sung (theo yêu cầu §9)

| Cấp | Test cần có | Ưu tiên |
|---|---|---|
| Unit | Golden-case PIT kỳ 2025 & 2026, mọi ngưỡng bậc (±1 đ), NPT 0..n, có/không BH, trần BHXH/BHYT/BHTN theo vùng | P0 |
| Unit | CIT: 15%/17%/20% theo doanh thu kỳ trước, biên 3 tỷ & 50 tỷ, từ chối ưu đãi không hồ sơ, lỗ quá 5 năm | P0 |
| Unit | Rule khấu trừ VAT: 4.999.999 / 5.000.000 (sau 01/07/2025), 19.999.999 / 20.000.000 (trước), cộng gộp cùng người bán trong ngày, trả góp | P0 |
| Unit | Chọn rule theo kỳ hiệu lực (ngày giao dịch rơi đúng `effective_from`/`effective_to`) | P0 |
| Unit | Làm tròn VND, `Decimal` | P1 |
| Unit | Hạn nộp: mọi quý, năm tài chính lệch, dời ngày nghỉ | P1 |
| Unit | Chống trùng: cùng số khác ký hiệu/năm; `0000123` vs `123`; MST có gạch | P1 |
| Unit | Thuế suất 8% các cửa sổ lịch sử | P1 |
| Integration | Hoá đơn → bút toán 1331/33311 → tổng hợp kỳ | P2 |
| Integration | Payroll → TNCN khấu trừ → 3335 | P2 |
| Integration | Sổ cái → TNDN | P2 |
| E2E | Lập tờ khai → phê duyệt (ADMIN) → trạng thái nộp → khai bổ sung | P2 |
| Security | Tool thuế không cấp cho agent ngoài phòng ban; capability `accounting.tax` khi agent thiếu tool; cross-tenant đọc `tax_calculations` | P1 |
| Dữ liệu mẫu | Bình thường, thiếu, sai, trùng, điều chỉnh, nhiều thuế suất, đổi luật theo thời gian, vượt quyền, lệch MISA–AIQ — chỉ dữ liệu tổng hợp `LIVE-TEST`, không dữ liệu khách hàng thật | P1 |
