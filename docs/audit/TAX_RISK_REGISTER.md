# AIQ — Sổ đăng ký rủi ro thuế (Tax Risk Register)

Ngày audit: 2026-11-13 · Nhánh: `feat/chung-tu-flow-v2` @ `b0d7aaf` · Phạm vi: VAT, TNCN, TNDN.
Báo cáo tổng: [TAX_WORKFLOW_AUDIT_REPORT.md](TAX_WORKFLOW_AUDIT_REPORT.md).

Quy ước: **LRR** = LEGAL_REVIEW_REQUIRED (cần chuyên gia thuế xác nhận căn cứ). Mọi mã test `VAT-xx / PIT-xx / CIT-xx / DL-xx` tham chiếu bảng kết quả trong [TAX_TEST_COVERAGE.md](TAX_TEST_COVERAGE.md).

## Tổng hợp

| Mức | Số rủi ro |
|---|---|
| CRITICAL | 3 |
| HIGH | 7 |
| MEDIUM | 8 |
| LOW | 4 |

---

## CRITICAL

### TAX-C01 — Công cụ TNCN dùng tham số kỳ 2025 cho kỳ tính thuế 2026
- **Module:** `calculate_pit` — [accounting_tools.py:195-247](../../agent/src/aiq_agent/agentic/accounting_tools.py#L195-L247), [accounting_tools.py:312-383](../../agent/src/aiq_agent/agentic/accounting_tools.py#L312-L383); skill [tax-calculation/SKILL.md:92-120](../../agent/skills/tax-calculation/SKILL.md#L92-L120).
- **Bằng chứng:** Hằng số ghi chú "PIT constants — 2026" nhưng dùng giảm trừ 11.000.000 / 4.400.000 (NQ 954/2020) và biểu 7 bậc. Từ kỳ tính thuế 2026: giảm trừ 15.500.000 / 6.200.000 (NQ 110/2025/UBTVQH15) và biểu 5 bậc 5/10/20/30/35% với ngưỡng 10/30/60/100 triệu (Luật 109/2025/QH15 Điều 9, áp dụng thu nhập tiền lương của cá nhân cư trú từ kỳ 2026). Probe PIT-01..05, PIT-07 FAIL. Ví dụ gross 30tr, 0 NPT: công cụ trả **1.627.500 ₫**, đúng là **635.000 ₫** (lệch +156%).
- **Tác động:** Khấu trừ thừa thuế TNCN của người lao động mỗi tháng; quyết toán 2026 sai; tư vấn sai cho người dùng.
- **Đề xuất:** Tách tham số TNCN ra bảng rule có `effective_from/effective_to/legal_reference/version`; chọn theo kỳ tính thuế (ngày trả thu nhập), giữ bộ 2025 cho quyết toán năm 2025. Bổ sung test biên cho cả hai kỳ. **LRR:** xác nhận điều khoản chuyển tiếp của Luật 109/2025 (biểu 5 bậc áp dụng từ 01/01/2026 hay 01/07/2026 cho tiền lương) — các nguồn thứ cấp không thống nhất.
- **Ưu tiên:** P0.

### TAX-C02 — Công cụ TNDN mặc định 20% và không kiểm tra điều kiện thuế suất
- **Module:** `calculate_cit` — [accounting_tools.py:385-436](../../agent/src/aiq_agent/agentic/accounting_tools.py#L385-L436); UI [tax-calc/page.tsx:15](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L15).
- **Bằng chứng:** `rate_pct` mặc định 20, chấp nhận bất kỳ giá trị trong {5,10,15,17,20} do người gọi/LLM chọn, không có dữ liệu doanh thu năm trước, không kiểm tra điều kiện. Luật 67/2025/QH15 (hiệu lực 01/10/2025, áp dụng từ kỳ 2025): 15% nếu tổng doanh thu năm ≤ 3 tỷ, 17% nếu > 3 tỷ đến 50 tỷ (xác định theo doanh thu kỳ trước). Probe CIT-01 (DT 10 tỷ → 400tr thay vì 340tr), CIT-02 (DT 2 tỷ → 100tr thay vì 75tr) FAIL; CIT-03: chấp nhận 17% cho DT 200 tỷ.
- **Tác động:** Tính thừa thuế cho phần lớn doanh nghiệp SME (nhóm khách hàng chính), hoặc tính thiếu khi LLM chọn sai mức ưu đãi.
- **Đề xuất:** Rule thuế suất có điều kiện (doanh thu kỳ trước, loại trừ DN có quan hệ liên kết theo luật) + version; thuế suất ưu đãi chỉ áp dụng khi có hồ sơ ưu đãi được ADMIN xác nhận. **LRR:** điều kiện loại trừ tại NĐ hướng dẫn Luật 67/2025.
- **Ưu tiên:** P0.

### TAX-C03 — Điều kiện khấu trừ VAT đầu vào sai ngưỡng và quá lỏng
- **Module:** [ExpenseRiskOptions.cs:10-12](../../gateway/src/Aiq.Gateway/Jobs/ExpenseRiskOptions.cs#L10-L12), [appsettings.json:52](../../gateway/src/Aiq.Gateway/appsettings.json#L52), [VoucherExtractJobRunner.cs:1564-1573](../../gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs#L1564-L1573), [IExpenseClaimRepository.cs:384-405](../../gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs#L384-L405).
- **Bằng chứng:**
  1. Ngưỡng chứng từ thanh toán không dùng tiền mặt cấu hình **30.000.000** với chú thích "TT78 điều 6 … mức luật hiện hành". Thực tế: 20 triệu trước 01/07/2025 (Luật GTGT 2008/TT219), **5 triệu (giá đã có thuế, cộng gộp trong ngày)** từ 01/07/2025 (Luật 48/2024/QH15 khoản 2 Điều 14, NĐ 181/2025). TT78/2021 quy định hoá đơn, không quy định điều kiện khấu trừ.
  2. Vi phạm ngưỡng chỉ tạo `risk_flag`, không loại VAT khỏi số được khấu trừ.
  3. Với đề nghị thanh toán (expense claim), VAT được coi là "được khấu trừ" chỉ cần dòng có `invoice_no` ([IExpenseClaimRepository.cs:394](../../gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs#L394)) — không kiểm tra MST người mua là công ty, hình thức thanh toán, trạng thái xác thực hoá đơn, mục đích phục vụ hoạt động chịu thuế. Số này được hạch toán Nợ 1331 khi ghi nhận chi phí.
- **Tác động:** Khấu trừ VAT đầu vào không đủ điều kiện → truy thu, phạt, tiền chậm nộp khi thanh tra.
- **Đề xuất:** Rule khấu trừ có version theo ngày hoá đơn (20tr / 5tr), cộng gộp theo người bán trong ngày, xét cả chứng từ thanh toán thực tế (cash_transactions/sao kê), MST người mua, trạng thái tax verification; kết quả "không đủ điều kiện" phải chuyển VAT vào chi phí/không khấu trừ chứ không chỉ cảnh báo.
- **Ưu tiên:** P0.

---

## HIGH

### TAX-H01 — Hạn nộp tờ khai sai và có nhiều biểu mẫu lỗi thời
- **Module:** `check_filing_deadline` — [accounting_tools.py:142-192](../../agent/src/aiq_agent/agentic/accounting_tools.py#L142-L192), [accounting_tools.py:514-548](../../agent/src/aiq_agent/agentic/accounting_tools.py#L514-L548); UI [deadline/page.tsx:24-60](../../web/src/app/(app)/accounting/deadline/page.tsx#L24-L60) (logic trùng lặp, cùng lỗi).
- **Bằng chứng:** Hạn khai quý cố định ngày **30** — đúng phải là ngày cuối cùng của tháng đầu quý sau (Luật QLT 38/2019 Điều 44): Q2→31/07, Q3→31/10, Q4→31/01 (DL-03..05 FAIL). `02/QTT-TNCN` (cá nhân tự quyết toán) trả 31/03, đúng là ngày cuối tháng thứ 4 = 30/04 (DL-06 FAIL). Không dời hạn khi trùng ngày nghỉ (ví dụ 30/04/2026 → 04/05/2026). `03/TNDN` giả định năm tài chính = năm dương lịch. Mục `02/TNDN` "Tờ khai tạm tính thuế TNDN" theo quý và quy tắc `05/KK-TNCN` "khai tháng nếu khấu trừ ≥100 triệu" là quy định cũ (trước NĐ 126/2020) — **LRR**. Luật Quản lý thuế 2025 hiệu lực 01/07/2026 — chưa đối chiếu.
- **Tác động:** Người dùng nộp muộn hoặc nhầm nghĩa vụ; phạt chậm nộp hồ sơ.
- **Đề xuất:** Một nguồn lịch nghĩa vụ duy nhất phía Gateway (bỏ logic trùng ở web), rule có version, hỗ trợ năm tài chính của tenant và lịch nghỉ lễ.
- **Ưu tiên:** P1.

### TAX-H02 — Chống trùng hoá đơn dùng khoá sai, tự động từ chối chứng từ hợp lệ
- **Module:** [IVoucherRepository.cs:1001-1023](../../gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs#L1001-L1023), [VoucherExtractJobRunner.cs:551-569](../../gateway/src/Aiq.Gateway/Jobs/VoucherExtractJobRunner.cs#L551-L569).
- **Bằng chứng:** Khoá trùng = `seller_mst + document_number` so khớp nguyên văn. Không có ký hiệu mẫu số/ký hiệu hoá đơn (số hoá đơn điện tử bắt đầu lại từ 1 theo từng ký hiệu/năm theo NĐ 123/2020, TT 78/2021), không chuẩn hoá số (`0000123` ≠ `123`), không chuẩn hoá MST. Khi trùng, chứng từ mới bị chuyển `rejected` tự động. Trong cùng hệ thống, `TaxVerificationRules.NormalizeNumber` đã chuẩn hoá đúng ([TaxVerification.cs:92-97](../../gateway/src/Aiq.Gateway/Vouchers/TaxVerification.cs#L92-L97)) nhưng không được dùng ở đây.
- **Tác động:** Hoá đơn hợp lệ năm sau của cùng người bán bị từ chối (thiếu chi phí/VAT đầu vào); hoá đơn trùng thật lọt qua khi định dạng số khác nhau (khấu trừ hai lần).
- **Đề xuất:** Khoá = MST người bán (chuẩn hoá) + ký hiệu mẫu số + ký hiệu + số (chuẩn hoá) [+ năm]; trùng → `needs_review`, không tự từ chối.
- **Ưu tiên:** P1.

### TAX-H03 — Kết quả thuế hiển thị là văn bản LLM, không gắn với kết quả engine; không có hồ sơ tính toán
- **Module:** [tax-calc/page.tsx:112-118, 132, 225, 314](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L112-L118); [audit/store.py:22-49](../../agent/src/aiq_agent/audit/store.py#L22-L49).
- **Bằng chứng:** UI ghép số người dùng nhập vào câu lệnh tự do → capability `accounting.tax` → LLM quyết định có gọi tool hay không → UI hiển thị **câu trả lời LLM** (không phải JSON của tool). Không có lưu trữ đầu vào, đầu ra, version rule, căn cứ pháp lý. Audit agent là Redis list giới hạn 5.000 bản ghi, chỉ lưu tên tool.
- **Tác động:** Số thuế không tái lập được, LLM có thể diễn giải sai hoặc không gọi tool; không đáp ứng yêu cầu kiểm toán.
- **Đề xuất:** Gọi engine trực tiếp qua API Gateway (không qua LLM) cho màn hình tính thuế; lưu `tax_calculations` (input, output, rule_version, legal_refs, actor, tenant/department); LLM chỉ giải thích dựa trên bản ghi đã lưu.
- **Ưu tiên:** P1.

### TAX-H04 — Không có quy trình VAT theo kỳ (tổng hợp, chuyển kỳ, tờ khai 01/GTGT)
- **Module:** không tồn tại (đã tìm `SUM(vat_amount)`, `01/GTGT`, `declaration`, `xml` trong Gateway/Agent/Web — chỉ thấy bảng hạn nộp).
- **Bằng chứng:** Không có bảng kê mua vào/bán ra, không có VAT đầu ra − đầu vào theo kỳ, không có số còn được khấu trừ chuyển kỳ, không có tờ khai/XML/trạng thái nộp/tờ khai bổ sung. `calculate_vat` chỉ tách giá–thuế một hoá đơn. Skill mô tả công thức thiếu khoản chuyển kỳ trước ([SKILL.md:63-70](../../agent/skills/tax-calculation/SKILL.md#L63-L70)).
- **Tác động:** Không thể dùng AIQ để kê khai VAT.
- **Đề xuất:** Xem roadmap giai đoạn 3.
- **Ưu tiên:** P2 (tính năng), nhưng chặn sản phẩm "kê khai thuế".

### TAX-H05 — Không có dữ liệu bảng lương / nhân sự cho TNCN
- **Module:** [IEmployeeRepository.cs:16-25](../../gateway/src/Aiq.Gateway/Employees/IEmployeeRepository.cs#L16-L25); [scripts/seed_demo_agents.sql:24-25](../../scripts/seed_demo_agents.sql#L24-L25).
- **Bằng chứng:** Hồ sơ nhân viên chỉ có mã, tên, phòng, chức vụ, email, ngân sách chi phí, tạm ứng. Không có MST cá nhân, loại hợp đồng, cư trú, NPT, thu nhập, BH, thuế đã khấu trừ. `calculate_payroll`, `lookup_employee` chỉ là mã trong catalog demo, không có handler (sẽ lỗi "no installed handler" nếu được cấp).
- **Tác động:** Quy trình TNCN (khấu trừ tháng/quý, quyết toán 05/QTT, chứng từ khấu trừ) **chưa triển khai**.
- **Ưu tiên:** P2.

### TAX-H06 — Dữ liệu TNDN và BCTC do LLM soạn từ ghi chú/văn bản dán vào, không lấy từ sổ cái
- **Module:** [reports/page.tsx:29-60](../../web/src/app/(app)/accounting/reports/page.tsx#L29-L60).
- **Bằng chứng:** BCKQHĐKD (gồm dòng "thuế TNDN") được tạo bằng prompt LLM từ Note KB hoặc dữ liệu người dùng dán. Không có sổ cái/bảng cân đối phát sinh (chỉ có `voucher_journal_suggestions` và bút toán posting). Tên công ty mặc định hardcode "Công ty TNHH Cát Tường AI" (dòng 31).
- **Tác động:** Không thể đối chiếu TNDN với sổ cái/BCTC; nguy cơ số liệu bịa.
- **Ưu tiên:** P2.

### TAX-H07 — Không có cơ chế version cho rule TNCN/TNDN/hạn nộp
- **Module:** [accounting_tools.py:142-219](../../agent/src/aiq_agent/agentic/accounting_tools.py#L142-L219).
- **Bằng chứng:** Chỉ VAT rate có bảng `vat_rates(effective_from, effective_to, legal_ref)` ([068_voucher_validation.sql:11-35](../../gateway/src/Aiq.Gateway/Data/Migrations/068_voucher_validation.sql#L11-L35)). Mọi tham số khác hardcode trong Python, không có kỳ hiệu lực, không có `version`. Không thể tính lại kỳ cũ đúng luật kỳ cũ (vi phạm yêu cầu §8).
- **Ưu tiên:** P0 (là điều kiện để sửa C01/C02/H01 đúng cách).

---

## MEDIUM

### TAX-M01 — Bảng thuế suất VAT thiếu các cửa sổ giảm 8% trước 07/2025; dòng gốc "chờ kế toán duyệt"
- **Bằng chứng:** [068_voucher_validation.sql:28-35](../../gateway/src/Aiq.Gateway/Data/Migrations/068_voucher_validation.sql#L28-L35) chỉ có 8% từ 01/07/2025–31/12/2026 (NĐ 174/2025 theo NQ 204/2025 — **khớp nguồn đã kiểm**). Hoá đơn 8% hợp lệ năm 2022–06/2025 sẽ bị `VAT_RATE` (Error) ([VoucherValidator.cs:167](../../gateway/src/Aiq.Gateway/Vouchers/Validation/VoucherValidator.cs#L167)) và bị chặn chuyển trạng thái ở tenant v2 ([VoucherLifecycleEndpoints.cs:91,118](../../gateway/src/Aiq.Gateway/Endpoints/VoucherLifecycleEndpoints.cs#L91)). Các dòng 0/5/10/KCT/KKKNT ghi `effective_from=2009-01-01`, legal_ref chung "Luật Thuế GTGT", note "chờ kế toán duyệt"; Luật 48/2024 đã thay đổi danh mục 5%/KCT từ 01/07/2025.
- **Đề xuất:** Bổ sung cửa sổ NQ 43/2022, NQ 101/2023, NQ 110/2023, NQ 142/2024 (**LRR** từng khoảng ngày); phân biệt rate theo nhóm hàng hoá (không chỉ theo con số). **Ưu tiên:** P1.

### TAX-M02 — Trần BHTN sai căn cứ
- **Bằng chứng:** [accounting_tools.py:218-219, 327-329](../../agent/src/aiq_agent/agentic/accounting_tools.py#L218-L219) áp trần 46,8tr (20× lương cơ sở) cho cả BHTN. Từ 01/01/2026, trần BHTN = 20× lương tối thiểu vùng (Luật Việc làm 2025; vùng I 5.310.000 theo NĐ 293/2025 → 106,2tr). Probe PIT-04 lệch 32.000 ₫ ở gross 50tr. Lương cơ sở sau 01/07/2026: **NOT VERIFIED**. Không có tham số vùng.
- **Ưu tiên:** P1.

### TAX-M03 — Số học dấu phẩy động, không có quy tắc làm tròn VND
- **Bằng chứng:** `float` toàn bộ engine; VAT-04 trả 112.233,36 ₫. Gateway dùng `decimal` nhưng engine Python không. **Ưu tiên:** P1.

### TAX-M04 — `calculate_cit` thiếu các thành phần bắt buộc của công thức
- **Bằng chứng:** [accounting_tools.py:401](../../agent/src/aiq_agent/agentic/accounting_tools.py#L401): `taxable = revenue - expense - loss`. Không có thu nhập khác, thu nhập miễn thuế, chi phí không được trừ (điều chỉnh tăng), giới hạn chuyển lỗ 5 năm, lỗ vượt thu nhập, số đã tạm nộp, ưu đãi/miễn giảm. Lỗ được trừ kể cả khi đã âm. **Ưu tiên:** P2.

### TAX-M05 — TNCN chỉ có trường hợp cư trú, lương tháng
- **Bằng chứng:** Không có không cư trú (20% toàn phần), khấu trừ 10% với HĐ < 3 tháng/không ký HĐ (≥ 2 triệu/lần), thu nhập miễn thuế (ăn ca, làm thêm giờ…), quyết toán năm. **Ưu tiên:** P2.

### TAX-M06 — Khấu trừ VAT trên đề nghị thanh toán bị phân bổ theo hạn mức chi
- **Bằng chứng:** [IExpenseClaimRepository.cs:392, 426-437](../../gateway/src/Aiq.Gateway/Employees/IExpenseClaimRepository.cs#L392). VAT được khấu trừ bị nhân tỷ lệ cắt theo chính sách chi tiêu nội bộ. Về luật, điều kiện khấu trừ gắn với hoá đơn/chứng từ, không với hạn mức hoàn ứng nội bộ — **LRR**. **Ưu tiên:** P2.

### TAX-M07 — Skill hướng dẫn LLM chứa thông tin pháp lý lỗi thời / sai
- **Bằng chứng:** [tax-calculation/SKILL.md](../../agent/skills/tax-calculation/SKILL.md): giảm trừ 11tr/4,4tr và biểu 7 bậc cho "2026" (dòng 94-120); trích "NQ 954/2020" làm căn cứ biểu thuế (NQ 954 chỉ quy định giảm trừ; biểu thuế thuộc Luật TNCN); bảng TNDN "17% DN nhỏ theo Nghị quyết / 15% siêu nhỏ" không khớp Luật 67/2025 (dòng 74-83); ví dụ net→gross tự mâu thuẫn (net 30,91tr nhưng kết luận "≈ 30tr ✓", dòng 174-181); công thức VAT phải nộp thiếu số chuyển kỳ trước. LLM dùng skill này khi giải thích. **Ưu tiên:** P1.

### TAX-M08 — Tool thuế chỉ được đăng ký trong script seed demo
- **Bằng chứng:** `mcp__aiq__calculate_vat/pit/cit/check_filing_deadline` chỉ có trong [scripts/seed_demo_agents.sql:17-21](../../scripts/seed_demo_agents.sql#L17-L21); không có trong migrations hay `infra/postgres/init`. Môi trường mới (không chạy seed demo) không có tool để cấp cho agent → capability `accounting.tax` có thể chạy agent không có tool và LLM tự tính. **NOT VERIFIED** trên DB thật (không truy vấn DB theo phạm vi audit). **Ưu tiên:** P1.

---

## LOW

### TAX-L01 — Câu cảnh báo phạt "2–25 triệu … NĐ125/2020" chưa cập nhật
- [accounting_tools.py:557-558](../../agent/src/aiq_agent/agentic/accounting_tools.py#L557-L558). NĐ 125/2020 đã được sửa đổi (NĐ 310/2025) — **LRR**.

### TAX-L02 — Cấu hình chết với căn cứ pháp lý đã hết hiệu lực
- [ExpenseRiskOptions.cs:21-23](../../gateway/src/Aiq.Gateway/Jobs/ExpenseRiskOptions.cs#L21-L23): `GiftEntertainmentBudgetPct = 15` trích "TT96/2015 … tối đa 15%". Mức khống chế 15% chi quảng cáo/tiếp khách đã bị bãi bỏ từ kỳ 2015 (Luật 71/2014/QH13) — **LRR**. Không được sử dụng ở đâu (grep), nhưng dễ bị dùng sai sau này.

### TAX-L03 — Prompt tính thuế ghép chuỗi từ input người dùng
- [tax-calc/page.tsx:132, 225, 314](../../web/src/app/(app)/accounting/tax-calc/page.tsx#L132). Rủi ro prompt-injection chỉ trong phiên của chính người dùng (tool đều read-only) — thấp, nhưng sẽ hết khi xử lý TAX-H03.

### TAX-L04 — `FindDuplicateAsync` cho phép `tenantId = NULL`
- [IVoucherRepository.cs:1014](../../gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs#L1014): `@tenantId IS NULL OR tenant_id = @tenantId` → nếu caller truyền null sẽ dò trùng xuyên tenant. Hai caller hiện tại đều truyền tenant; **NOT VERIFIED** có đường gọi null.

---

## Ghi chú về phân tách nhiệm vụ (không phải lỗi)

Yêu cầu audit §7 "người lập không tự phê duyệt" xung đột với quyết định sản phẩm cố định **ADMIN Department Authority Invariant** ([CLAUDE.md](../../CLAUDE.md), [ACL_V2_RESOURCE_VISIBILITY_RULES.md §15](../ACL_V2_RESOURCE_VISIBILITY_RULES.md)): ADMIN cùng phòng ban được thực hiện toàn bộ quy trình, kể cả với bản ghi do chính mình tạo. Audit **không** ghi nhận đây là lỗi. Nếu tờ khai thuế (khi được xây dựng) cần phân tách người lập/người ký nộp, đó là **quyết định sản phẩm cần anh/chị phê duyệt rõ ràng** trước khi thiết kế.
