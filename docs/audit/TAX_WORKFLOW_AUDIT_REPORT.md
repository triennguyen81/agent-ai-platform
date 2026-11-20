# AIQ — Báo cáo audit quy trình thuế VAT · TNCN · TNDN

- **Ngày:** 2026-11-13
- **Mã nguồn:** nhánh `feat/chung-tu-flow-v2`, commit `b0d7aaf`
- **Phạm vi:** Gateway ASP.NET Core, Agent FastAPI/Claude Agent SDK, skills, PostgreSQL migrations, Web Next.js, seed scripts.
- **Ràng buộc:** audit độc lập — không sửa mã, không migration, không ghi DB, không deploy.
- **Báo cáo chi tiết:** [VAT](VAT_AUDIT_REPORT.md) · [TNCN](PIT_AUDIT_REPORT.md) · [TNDN](CIT_AUDIT_REPORT.md) · [Test](TAX_TEST_COVERAGE.md) · [Rủi ro](TAX_RISK_REGISTER.md) · [Lộ trình](TAX_REMEDIATION_ROADMAP.md)

---

## A. Executive Summary

**AIQ hiện chưa có quy trình thuế nào sẵn sàng đưa vào sử dụng để tính hoặc kê khai thuế.** Phần có thật và có chất lượng là **kiểm soát chứng từ đầu vào**: validator xác định cho hoá đơn (MST, cộng tiền, thuế suất theo ngày), cổng xác thực hoá đơn chỉ ADMIN chứng thực, khoá kỳ, và Agent bị giới hạn ở mức đề xuất. Phần **tính thuế** chỉ gồm ba "máy tính" một dòng trong Agent (`calculate_vat/pit/cit`), không lấy dữ liệu từ sổ sách, không lưu kết quả, và đi qua LLM trước khi hiển thị. Không có tổng hợp VAT theo kỳ, không có bảng lương, không có sổ cái cho TNDN, không có tờ khai, không có MISA.

| Quy trình | Đánh giá tổng | Mức sẵn sàng |
|---|---|---|
| VAT | Kiểm tra hoá đơn: PARTIAL→PASS · Khấu trừ: **FAIL** · Tính kỳ/kê khai: NOT IMPLEMENTED | Không sẵn sàng |
| TNCN | Engine: **FAIL** (sai kỳ 2026) · Dữ liệu, quyết toán, đối chiếu: NOT IMPLEMENTED | Không sẵn sàng |
| TNDN | Engine: **FAIL** (thuế suất) · Dữ liệu sổ cái, quyết toán: NOT IMPLEMENTED | Không sẵn sàng |

**Rủi ro nghiêm trọng nhất (CRITICAL):**
1. **TAX-C01** — Engine TNCN dùng giảm trừ 11tr/4,4tr và biểu 7 bậc cho kỳ 2026. Đúng là 15,5tr/6,2tr (NQ 110/2025) và biểu 5 bậc (Luật 109/2025). Gross 30tr: engine 1.627.500 ₫, đúng 635.000 ₫.
2. **TAX-C02** — Engine TNDN mặc định 20%, không kiểm tra điều kiện. Luật 67/2025: 15% (DT ≤ 3 tỷ), 17% (3–50 tỷ). DT 10 tỷ: engine 400tr, đúng 340tr; engine chấp nhận 17% cho DT 200 tỷ.
3. **TAX-C03** — Ngưỡng thanh toán không dùng tiền mặt để khấu trừ VAT cấu hình 30tr (đúng: 5tr từ 01/07/2025, 20tr trước đó) và chỉ là cờ cảnh báo; VAT trên đề nghị thanh toán được coi là được khấu trừ chỉ vì có số hoá đơn.

Kiểm thử: probe golden-case 26 ca → **12 PASS / 14 FAIL**; 159 test sẵn có liên quan đều PASS nhưng **không có test nào cho ba engine thuế**.

---

## B. Architecture Mapping

| Hạng mục | VAT | TNCN | TNDN |
|---|---|---|---|
| **Agent** | Agent phòng ban được bind capability `accounting.tax` (dynamic, [CapabilityEndpoints.cs](../../gateway/src/Aiq.Gateway/Endpoints/CapabilityEndpoints.cs)); demo `kt-bookkeeper`, `vat-quick-check` ([seed_demo_agents.sql](../../scripts/seed_demo_agents.sql)); agent chứng từ "Trợ lý kế toán" cho trích xuất/đề xuất | như VAT | như VAT |
| **Skills** | `tax-calculation`, `voucher-invoice-vat`, `accounting-vietnam` | `tax-calculation` | `tax-calculation` |
| **Tools (Agent)** | `calculate_vat`, `check_filing_deadline`, `verify_einvoice_tct`, `propose_tax_verification`, `reject_tax_verified`, `suggest_journal_entry`, `save_journal_suggestions` | `calculate_pit`, `check_filing_deadline` | `calculate_cit`, `check_filing_deadline` |
| **API (Gateway)** | `/vouchers/*` (validate, lifecycle, tax-verification), `/accounting-periods`, `/me/capabilities/{code}/agent` | không có | không có |
| **Database** | `vouchers`, `voucher_line_items`, `vat_rates`, `voucher_validations`, `voucher_tax_verification_events`, `expense_claim_results.vat_*`, `voucher_journal_suggestions`, `accounting_periods` | không có bảng thuế/lương | `expense_claim_results.non_deductible_amount` (duy nhất) |
| **Workflow** | Trích xuất → validator → (v2) lifecycle gate → ADMIN xác thực HĐ → bút toán đề xuất/posting | không có | không có |
| **UI** | `/accounting/tax-calc` (tab VAT), `/accounting/deadline`, panel xác thực trên chứng từ | `/accounting/tax-calc` (tab TNCN) | `/accounting/tax-calc` (tab TNDN), `/accounting/reports` (LLM) |
| **Test** | Validator, rules, tax verification (unit + integration sẵn có) | 0 | 0 |
| **MISA** | Không có | Không có | Không có |

Ghi chú kiến trúc:
- **Không có** Tax Manager / VAT / PIT / CIT / Tax Rule / Tax Declaration agent riêng; tất cả là một capability `accounting.tax` → một agent phòng ban với bộ tool. Điều này **không phải lỗi** (yêu cầu cho phép cấu trúc tương đương), nhưng không có lớp "rule" hay "declaration" nào để phân tách.
- Orchestration Planner–Executor–Critic có trong [orchestration/pec.py](../../agent/src/aiq_agent/orchestration/pec.py) nhưng đường `accounting.tax` đi qua manifest executor ([runtime/executors.py](../../agent/src/aiq_agent/runtime/executors.py)) — không có bước critic đối chiếu câu trả lời LLM với output tool.
- Redis chỉ dùng cho audit Agent ([audit/store.py](../../agent/src/aiq_agent/audit/store.py)); pgvector/KB không chứa rule thuế có cấu trúc.

---

## C. Business Process Audit

Chi tiết từng dòng trong báo cáo riêng. Tổng hợp:

| Mục | VAT | TNCN | TNDN |
|---|---|---|---|
| Thu thập dữ liệu | PARTIAL (OCR HĐ; không XML, không MISA, không HĐ điều chỉnh) | NOT IMPLEMENTED | PARTIAL (chỉ chi phí từ đề nghị thanh toán) |
| Kiểm tra chứng từ | PARTIAL — MST/cộng tiền/thuế suất PASS; **chống trùng FAIL** | NOT IMPLEMENTED | PARTIAL (cờ không được trừ khi thiếu HĐ) |
| Phân loại / điều kiện | **FAIL** (khấu trừ đầu vào) | NOT IMPLEMENTED (cư trú, loại HĐ) | **FAIL** (điều kiện thuế suất) |
| Tính thuế | Tách giá–thuế PASS; tính kỳ NOT IMPLEMENTED | **FAIL** (kỳ 2026) | **FAIL** (thuế suất), thiếu thành phần |
| Kê khai / quyết toán | NOT IMPLEMENTED; hạn nộp **FAIL** | NOT IMPLEMENTED; hạn 02/QTT **FAIL** | NOT IMPLEMENTED; 02/TNDN lỗi thời (LRR) |
| Đối chiếu | PARTIAL (`JOURNAL_TOTAL` cảnh báo) | NOT IMPLEMENTED | **FAIL** (BCTC do LLM soạn) |

---

## D. Tax Calculation Audit

| Sắc thuế | Công thức hiện tại | Công thức đúng | Kết quả test | Sai lệch | Nguyên nhân |
|---|---|---|---|---|---|
| VAT (1 HĐ) | `vat = net × r`; `net = gross/(1+r)`, float | như trên, làm tròn VND | 5/6 PASS | VAT-04: 0,36 ₫ không làm tròn | float, không quy tắc làm tròn |
| VAT (kỳ) | không có | Đầu ra − đầu vào đủ ĐK − chuyển kỳ trước ± điều chỉnh | — | — | chưa triển khai |
| TNCN | `TNTT = gross − 10,5%×min(gross,46,8tr) − 11tr − 4,4tr×NPT`; biểu 7 bậc | Kỳ 2026: giảm trừ 15,5tr/6,2tr; biểu 5 bậc 10/30/60/100tr; BHXH/BHYT trần 20× lương cơ sở, BHTN trần 20× LTTV vùng; trừ thu nhập miễn thuế | 1/7 PASS | +992.500 ₫/tháng (gross 30tr) đến +2.191.800 ₫ (gross 50tr, 2 NPT) | Hằng số kỳ 2025, không chọn rule theo kỳ |
| TNDN | `(DT − CP − lỗ) × rate`, rate mặc định 20 | `(DT − CP được trừ + TN khác − miễn thuế − lỗ ≤ 5 năm) × thuế suất theo điều kiện − ưu đãi − đã tạm nộp` | 2/5 PASS | +60tr (DT 10 tỷ), +25tr (DT 2 tỷ) | Thuế suất không có điều kiện; thiếu thành phần |
| Hạn nộp | quý = ngày 30; năm = 31/03 | Ngày cuối tháng đầu quý sau; QT cá nhân cuối tháng thứ 4; dời ngày nghỉ; năm tài chính tenant | 4/8 PASS | 1 ngày (Q2/Q3/Q4), 1 tháng (02/QTT-TNCN) | Hardcode |

Tính xác định: ba engine là hàm Python thuần (tốt), **nhưng** đường người dùng là: input → prompt tự do → LLM → (có thể) gọi tool → LLM viết lại → UI hiển thị văn bản LLM ([tax-calc/page.tsx:112-132](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L112-L132)). Kết quả không lưu, không tái lập.

---

## E. Legal Compliance Audit

Nguồn tra cứu ngày 2026-11-13 qua web (chủ yếu trang tổng hợp: Thư viện pháp luật, MISA, KPMG, EY, VCCI); chưa đối chiếu trực tiếp Công báo. Mọi dòng **LRR** cần chuyên gia thuế xác nhận.

| Văn bản | Hiệu lực | Rule trong AIQ | Đánh giá |
|---|---|---|---|
| NQ 954/2020/UBTVQH14 (giảm trừ 11tr/4,4tr) | 01/07/2020 → hết kỳ 2025 | `PIT_PERSONAL_DEDUCTION=11_000_000`, `PIT_DEPENDENT_DEDUCTION=4_400_000` | Đúng cho kỳ ≤ 2025; **sai** cho 2026 |
| NQ 110/2025/UBTVQH15 (15,5tr/6,2tr) | Từ kỳ tính thuế 2026 | không có | **Thiếu** — C01 |
| Luật TNCN 109/2025/QH15 (biểu 5 bậc, Đ9) | Hiệu lực 01/07/2026; quy định tiền lương cá nhân cư trú áp dụng kỳ 2026 | không có | **Thiếu** — C01 · **LRR** ngày áp dụng biểu |
| Luật TNDN 67/2025/QH15 (20%; 15% ≤ 3 tỷ; 17% 3–50 tỷ) | 01/10/2025, áp dụng kỳ 2025 | rate tự chọn, mặc định 20 | **Sai** — C02 · **LRR** điều kiện loại trừ (NĐ hướng dẫn) |
| Luật GTGT 48/2024/QH15 + NĐ 181/2025 (khấu trừ: chứng từ không tiền mặt từ 5tr) | 01/07/2025 | `CashPaymentVatDeductibleThreshold=30_000_000` | **Sai** — C03 (trước đó là 20tr, cũng không phải 30tr) |
| NQ 204/2025/QH15 + NĐ 174/2025 (giảm còn 8%) | 01/07/2025 – 31/12/2026 | `vat_rates` ('8', 2025-07-01, 2026-12-31, 'NĐ 174/2025/NĐ-CP') | **Đúng** |
| NQ 43/2022, NQ 101/2023, NQ 110/2023, NQ 142/2024 (8% các kỳ trước) | 02/2022 – 06/2025 (từng đợt) | không có | **Thiếu** — M01 · **LRR** từng khoảng ngày |
| Luật QLT 38/2019 Đ44 (hạn khai quý: ngày cuối tháng đầu quý sau; QT cá nhân: ngày cuối tháng thứ 4) | 01/07/2020 | quý = ngày 30; 02/QTT = 31/03 | **Sai** — H01 |
| Luật QLT 2025 | 01/07/2026 | không có | **NOT VERIFIED** — chưa đối chiếu hạn nộp mới · LRR |
| NĐ 126/2020 (không tờ khai TNDN tạm tính quý; tạm nộp 4 quý ≥ 80%) | 05/12/2020 | Mục "02/TNDN Tờ khai tạm tính" | **LRR** — nghi lỗi thời |
| NĐ 293/2025 (LTTV vùng I 5.310.000) + Luật Việc làm 2025 (trần BHTN 20× LTTV) | 01/01/2026 | trần 46,8tr cho cả BHTN | **Sai** — M02 |
| Lương cơ sở 2.340.000 (trần BHXH/BHYT 46,8tr) | 01/07/2024 → ? | 46.800.000 | **NOT VERIFIED** sau 01/07/2026 |
| Luật 71/2014/QH13 (bãi bỏ khống chế 15% chi quảng cáo/tiếp khách) | kỳ 2015 | `GiftEntertainmentBudgetPct=15` (không dùng) | **Lỗi thời** — L02 · LRR |
| NĐ 125/2020 sửa đổi bởi NĐ 310/2025 (xử phạt) | — | câu "2–25 triệu" | **LRR** — L01 |

Kết luận §8 yêu cầu `effective_from/effective_to/legal_reference/version` cho mỗi rule: chỉ **bảng VAT rate** đạt một phần (có hiệu lực + legal_ref, **không có version**, các dòng gốc "chờ kế toán duyệt"). TNCN, TNDN, khấu trừ VAT, hạn nộp: **không đạt**. Hệ thống **không thể** tính lại kỳ cũ theo luật kỳ cũ.

Nguồn: [KPMG — tăng giảm trừ từ kỳ 2026](https://kpmg.com/vn/vi/home/phan-tich-chuyen-sau/2025/11/increase-pit-relief-from-tax-year-2026.html) · [VnEconomy — chưa áp dụng 15,5tr khi QT 2025](https://vneconomy.vn/chua-ap-dung-muc-giam-tru-155-trieu-dong-khi-quyet-toan-thue-nam-2025.htm) · [EY — Luật TNCN 109/2025](https://www.ey.com/vi_vn/technical/tax/tax-and-law-updates/luat-thue-thu-nhap-ca-nhan-109-2025-qh15) · [TVPL — biểu 5 bậc](https://thuvienphapluat.vn/ma-so-thue/phap-luat-thue/cong-thuc-tinh-thue-tncn-theo-bieu-thue-luy-tien-5-bac-moi-nhat-2026-80072-216563.html) · [LuatVietnam — thuế suất TNDN Luật 67/2025](https://luatvietnam.vn/thue-phi-le-phi/thue-suat-thue-tndn-moi-nhat-theo-luat-so-67-2025-qh15-565-104201-article.html) · [MISA — Luật GTGT 48/2024](https://sme.misa.vn/344875/luat-thue-gtgt-so-48-2024-qh15/) · [MISA — NĐ 174/2025 giảm GTGT](https://amis.misa.vn/184663/nghi-dinh-giam-thue-gtgt-2025/) · [TVPL — mức đóng BHXH 2026](https://thuvienphapluat.vn/phap-luat-doanh-nghiep/bai-viet/cap-nhat-muc-dong-bhxh-2026-cua-tat-ca-cac-doi-tuong-17767.html) · [TVPL — hạn quyết toán TNCN](https://thuvienphapluat.vn/ma-so-thue/phap-luat-thue/thoi-han-nop-ho-so-khai-quyet-toan-thue-tncn-la-khi-nao-392483-170612.html)

---

## F. Security & Workflow Audit

### Vai trò thực tế
Hệ thống có 4 vai trò: `super_admin`, `tenant_owner`, `admin` (ADMIN phòng ban), `user` ([Program.cs:384-386](../../gateway/src/Aiq.Gateway/Program.cs#L384-L386)). **Không có** ACCOUNTANT / CHIEF_ACCOUNTANT / TAX_AUTHORIZED_USER / AUDITOR. Theo §7 không tạo role mới nếu permission + workflow đáp ứng được — hiện chưa có workflow thuế nào để gắn permission.

### Đối chiếu yêu cầu §6–§7

| Kiểm tra | Kết quả | Bằng chứng |
|---|---|---|
| Agent truy cập đúng tenant | PASS (tool thuế không đọc dữ liệu); TaxVerification lọc tenant+department | [TaxVerification.cs:249-253](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs#L249-L253) |
| Allowlist tool fail-closed | PASS | [runner.py:140-157](../../agent/src/aiq_agent/agentic/runner.py#L140-L157); legacy run tắt mặc định ([config.py:98-101](../../agent/src/aiq_agent/config.py#L98-L101)) |
| Tính bằng engine xác định | PARTIAL — hàm xác định có, nhưng kết quả hiển thị là văn bản LLM | TAX-H03 |
| Rule theo kỳ | **FAIL** | TAX-H07 |
| Ghi nguồn dữ liệu và căn cứ | **FAIL** — không lưu input/output/căn cứ | TAX-H03 |
| Kiểm tra kết quả trước khi trả (critic) | NOT IMPLEMENTED trên đường `accounting.tax` | |
| Audit log | PARTIAL — Redis capped 5.000, chỉ tên tool; sự kiện xác thực HĐ thì đầy đủ (bảng events) | [audit/store.py:22](../../agent/src/aiq_agent/audit/store.py#L22) |
| Agent tự xác thực / kết luận HĐ | PASS — chỉ đề xuất | [TaxVerification.cs:414](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs#L414) |
| Agent tự ghi sổ | PASS — chỉ `save_journal_suggestions` (đề xuất); posting là thao tác người dùng | voucher_tools.py |
| Agent tự nộp tờ khai | N/A — không có chức năng tờ khai | |
| Thay đổi sau phê duyệt → phê duyệt lại | PASS cho xác thực HĐ (fingerprint); N/A cho thuế | [TaxVerification.cs:451-470](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs#L451-L470) |
| Tờ khai đã nộp không bị ghi đè | N/A | |
| Tool thuế được đăng ký qua migration | **FAIL/NOT VERIFIED** — chỉ seed demo | TAX-M08 |
| Tenant A đọc dữ liệu thuế tenant B | N/A (không có dữ liệu thuế); dò trùng có nhánh `tenantId NULL` | TAX-L04 |

### Phân tách nhiệm vụ
Theo **ADMIN Department Authority Invariant** ([CLAUDE.md](../../CLAUDE.md)), ADMIN cùng phòng ban được làm toàn bộ quy trình kể cả với bản ghi do mình tạo — audit không coi đây là lỗi. USER không tự phê duyệt nghiệp vụ kế toán có thẩm quyền; TENANT_OWNER/SUPER_ADMIN không phê duyệt nghiệp vụ kế toán. Nếu sau này tờ khai thuế cần tách người lập/người ký nộp, cần **quyết định sản phẩm riêng**.

---

## G. Test Results

| | Tổng | PASS | FAIL | SKIPPED | NOT VERIFIED |
|---|---|---|---|---|---|
| Probe engine thuế | 26 | 12 | 14 | 0 | 0 |
| Agent pytest liên quan | 34 | 34 | 0 | 0 | 0 |
| Gateway unit liên quan | 125 | 125 | 0 | 0 | 0 |
| Gateway integration có DB | — | — | — | toàn bộ (ràng buộc không ghi DB) | — |
| E2E / integration MISA, payroll, GL, declaration | — | — | — | — | toàn bộ (không tồn tại hoặc cần phê duyệt) |

Test chưa có: toàn bộ engine thuế, rule theo kỳ, khấu trừ VAT, hạn nộp, chống trùng có ký hiệu, security cho capability thuế. Chi tiết: [TAX_TEST_COVERAGE.md](TAX_TEST_COVERAGE.md).

---

## H. Risk Register

Xem đầy đủ: [TAX_RISK_REGISTER.md](TAX_RISK_REGISTER.md).

| Mã | Mức | Mô tả ngắn |
|---|---|---|
| TAX-C01 | CRITICAL | TNCN dùng tham số 2025 cho kỳ 2026 |
| TAX-C02 | CRITICAL | TNDN mặc định 20%, không kiểm tra điều kiện 15%/17% |
| TAX-C03 | CRITICAL | Khấu trừ VAT: ngưỡng 30tr (đúng 5tr), chỉ cảnh báo; expense claim "có số HĐ = được khấu trừ" |
| TAX-H01 | HIGH | Hạn nộp sai, biểu mẫu lỗi thời, logic trùng ở web |
| TAX-H02 | HIGH | Chống trùng thiếu ký hiệu/chuẩn hoá, tự từ chối HĐ hợp lệ |
| TAX-H03 | HIGH | Kết quả hiển thị là văn bản LLM; không lưu hồ sơ tính toán |
| TAX-H04 | HIGH | Không có quy trình VAT theo kỳ / 01/GTGT |
| TAX-H05 | HIGH | Không có dữ liệu payroll cho TNCN |
| TAX-H06 | HIGH | BCTC/TNDN do LLM soạn, không từ sổ cái |
| TAX-H07 | HIGH | Không có version rule cho TNCN/TNDN/hạn nộp |
| TAX-M01..M08 | MEDIUM | 8% lịch sử, trần BHTN, float, thiếu thành phần TNDN, TNCN không cư trú, VAT bị cắt theo hạn mức, skill lỗi thời, tool chỉ trong seed demo |
| TAX-L01..L04 | LOW | Câu phạt, cấu hình 15% chết, prompt ghép chuỗi, dò trùng tenant NULL |

---

## I. Remediation Roadmap

Chi tiết: [TAX_REMEDIATION_ROADMAP.md](TAX_REMEDIATION_ROADMAP.md).

0. Chuyên gia thuế xác nhận các dòng **LRR** ở mục E.
1. **Sai công thức:** rule store có version (mở rộng `vat_rates`); engine TNCN/TNDN chuyển sang Gateway (`decimal`); tool Agent gọi engine — C01, C02, H07, M02, M03.
2. **Sai quy định:** rule khấu trừ VAT, lịch nghĩa vụ, 8% lịch sử, cập nhật skill — C03, H01, M01, M07, L01, L02.
3. **Dữ liệu & đối chiếu:** khoá chống trùng, sổ VAT theo kỳ, HĐ điều chỉnh, sổ cái cho TNDN, quyết định payroll/MISA — H02, H04, H05, H06.
4. **Phân quyền & tenant:** đăng ký tool qua migration, capability từ chối khi thiếu tool, test security — M08, L04.
5. **Phê duyệt & audit trail:** bảng `tax_calculations` bất biến; vòng đời tờ khai — H03.
6. **Báo cáo & UI:** màn hình tính thuế gọi engine trực tiếp; BCTC từ sổ — H03, H06, L03.
7. **Nâng cao:** 01/GTGT + XML, module TNCN đầy đủ, TNDN tạm nộp/chuyển lỗ/03/TNDN, MISA.
