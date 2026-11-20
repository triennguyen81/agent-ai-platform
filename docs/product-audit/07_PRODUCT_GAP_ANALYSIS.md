# 07 — Phân tích khoảng trống sản phẩm

Sổ hợp nhất, đã khử trùng các phát hiện ở [03](03_ROLE_PERMISSION_MATRIX.md), [04](04_ACCOUNTING_WORKFLOW_AUDIT.md), [05](05_AI_AGENT_AUDIT.md), [06](06_SECURITY_AND_DATA_ISOLATION.md) và audit thuế [docs/audit/TAX_RISK_REGISTER.md](../audit/TAX_RISK_REGISTER.md). Cột "Ảnh hưởng demo" cho biết lỗi có lộ ra trong kịch bản demo không.

**Tổng: 36 mục — CRITICAL 3 · HIGH 12 · MEDIUM 14 · LOW 7.**

## CRITICAL

| Mã | Khoảng trống | Module | Bằng chứng | Nguồn | Ảnh hưởng demo | Hướng xử lý |
|---|---|---|---|---|---|---|
| G-01 | Máy tính TNCN dùng giảm trừ 11tr/4,4tr và biểu 7 bậc cho kỳ 2026 (đúng: 15,5tr/6,2tr, biểu 5 bậc — LRR ngày áp dụng biểu) | Thuế | probe PIT-01..07, chat C04 | TAX-C01, AGT-01 | Có, nếu hỏi TNCN | Rule có version theo kỳ; engine ở Gateway (`decimal`) |
| G-02 | Máy tính TNDN mặc định 20%, không xét 15% (DT ≤3 tỷ) / 17% (3–50 tỷ) theo Luật 67/2025 | Thuế | probe CIT-01..03 | TAX-C02 | Có, nếu hỏi TNDN | Rule thuế suất có điều kiện |
| G-03 | Điều kiện khấu trừ VAT: ngưỡng không dùng tiền mặt cấu hình 30tr (đúng 5tr từ 01/07/2025), chỉ là cờ cảnh báo; VAT trên đề nghị thanh toán "được khấu trừ" chỉ vì có số HĐ | Thuế / Chi phí | `ExpenseRiskOptions.cs:12`, `IExpenseClaimRepository.cs:394` | TAX-C03 | Có (nhãn "Chi TM > 30M (vi phạm TT78)" ở hàng đợi duyệt) | Rule khấu trừ theo ngày HĐ, xét chứng từ thanh toán thực tế |

## HIGH

| Mã | Khoảng trống | Module | Bằng chứng | Nguồn | Ảnh hưởng demo | Hướng xử lý |
|---|---|---|---|---|---|---|
| G-04 | Hàng đợi duyệt L2 `/approval-queue` dùng policy legacy `admin`: TENANT_OWNER duyệt được (trái `CLAUDE.md`), ADMIN duyệt chứng từ phòng khác | Phê duyệt | `ApprovalEndpoints.cs:23-27`; API TO → 200 | ACC-01 | Có nếu demo bằng TO | Chuyển sang `department_admin` + kiểm tra phòng ban của chứng từ |
| G-05 | Đăng ký công khai tạo USER trong tenant `default` | Xác thực | `AuthEndpoints.cs:109-150`, `/signup` 200 | SEC-01 | Không | Tắt hoặc chỉ qua lời mời |
| G-06 | DLP tắt trong khi UI khẳng định PII luôn được che | DLP | `appsettings.json:57-60`, `sim_dlp_events`=0 | SEC-02 | Có nếu mở trang DLP | Bật DLP; UI hiển thị trạng thái thật |
| G-07 | Chat không đi qua DLP (web → Agent trực tiếp) | DLP / Chat | `web/src/app/api/chat/route.ts:138` | SEC-03 | Không lộ | Che PII trong Agent |
| G-08 | Postgres, Prometheus, Grafana, Gateway, Web mở `0.0.0.0` (bỏ qua Caddy TLS) | Hạ tầng | `docker ps` | SEC-04 | Không | Chỉ public Caddy |
| G-09 | Hạn nộp tờ khai sai (quý = ngày 30; 02/QTT-TNCN = 31/03; không dời ngày nghỉ; logic trùng ở web) | Thuế | probe DL-03..06, chat C07 | TAX-H01 | Có (trang Deadline) | Một lịch nghĩa vụ ở Gateway |
| G-10 | Dò trùng HĐ theo `seller_mst + số` (không ký hiệu/năm, không chuẩn hoá) và **tự từ chối** chứng từ | Chứng từ | `IVoucherRepository.cs:1001-1023`, `VoucherExtractJobRunner.cs:551-569` | TAX-H02, ACC-04 | Có thể (tải lại HĐ mẫu sẽ bị từ chối) | Khoá đúng + đưa về review |
| G-11 | Không có sổ cái/CĐPS; BCTC (kể cả dòng thuế TNDN) do LLM soạn từ KB/dữ liệu dán tay | Kế toán / Báo cáo | `accounting/reports/page.tsx` | ACC-02, ACC-03, TAX-H06 | Có nếu demo BCTC | Sổ cái từ posting; BCTC từ sổ |
| G-12 | Không lưu đầu vào/đầu ra/căn cứ của Agent; audit Redis giới hạn 5.000, chỉ tên tool | Agent / Audit | `audit/store.py:22-49` | AGT-02, TAX-H03 | Có nếu khách hỏi "AI tính dựa trên gì" | Bảng hồ sơ tính toán bất biến |
| G-13 | Cờ `voucher_flow_v2` chưa bật cho tenant nào → nộp/duyệt/trả lại/từ chối, khoá kỳ, chặn theo validator đều tắt | Chứng từ / Phê duyệt | `tenant_feature_flags` rỗng; `VoucherLifecycleEndpoints.cs:67` | ACC-08 | **Có** (bước "phê duyệt" của demo) | Bật cờ cho tenant demo sau khi kiểm thử |
| G-14 | Dashboard chỉ là thẻ liên kết; 7 trang Báo cáo/Usage/Audit (tenant, phòng ban, hệ thống) là cùng một trang nhật ký Agent | Dashboard / Báo cáo | `tenant/reports/page.tsx` … (1 dòng re-export) | DSH-01, RPT-02, USG-02 | **Có** (bước "Dashboard & báo cáo") | Dashboard số liệu + trang báo cáo thật (đã có API báo cáo tuần) |
| G-15 | Không có kê khai thuế (01/GTGT, 05/QTT, 03/TNDN), VAT theo kỳ, dữ liệu lương cho TNCN | Thuế | không có mã | TAX-H04, TAX-H05 | Không nếu không hứa | Roadmap P3 |

## MEDIUM

| Mã | Khoảng trống | Module | Nguồn | Ảnh hưởng demo |
|---|---|---|---|---|
| G-16 | Gateway chạy `Development`; OpenAPI bật nhưng trả 500 | Hạ tầng | SEC-05, MON-06 | Không |
| G-17 | Khoá JWT mẫu vượt qua kiểm tra độ dài | Bảo mật | SEC-06 | Không |
| G-18 | `AgentInternalNetworks` chưa cấu hình → mất tín hiệu nhận diện Agent ở Gateway | Bảo mật / Agent | SEC-07, AGT-03 | Không |
| G-19 | `audit_log` không bất biến, không có API/UI đọc | Audit | SEC-08, AUD-01 | Có (bước "Audit Log" chỉ hiển thị được nhật ký Agent) |
| G-20 | Upload không kiểm tra magic bytes | Bảo mật | SEC-09 | Không |
| G-21 | Agent `/metrics` 404 → Prometheus target down | Giám sát | MON-03 | Không |
| G-22 | Agent thử nghiệm ("Onboarding Smoke", "zzz") ở trạng thái production, gán cho USER và được chọn mặc định | Agent / Dữ liệu demo | AGT-06 | **Có** |
| G-23 | Agent nền tảng "Trợ lý kế toán" gán cho USER tenant Test 123 không có tool dữ liệu | Agent | AGT-07 | Có nếu demo bằng tenant Test 123 |
| G-24 | Skill `tax-calculation` và kiến thức model về thuế lỗi thời (11tr, ngưỡng 20tr…) | Agent / Thuế | AGT-05, TAX-M07 | Có |
| G-25 | Không có bước Critic trên luồng chat chính | Agent | AGT-04 | Không |
| G-26 | Kỳ kế toán không có UI | Kế toán | ACC-07 | Không |
| G-27 | Bảng thuế suất VAT thiếu các đợt giảm 8% trước 07/2025 → HĐ 8% cũ báo lỗi | Thuế | TAX-M01 | Có nếu dùng HĐ cũ |
| G-28 | Dữ liệu demo lẫn bản ghi `LIVE-TEST`/thử; hồ sơ công ty tenant Test 123 tên "Công ty TNHH Sáu Trực", MST 9 số; tài khoản demo dùng chung mật khẩu yếu (SEC-13) | Dữ liệu demo | §Live, SEC-13 | **Có** |
| G-29 | Không có tích hợp MISA/ERP, đọc XML HĐĐT, email/Zalo | Tích hợp | INT-01, OCR-04, NOT-03 | Không nếu không hứa |

## LOW

| Mã | Khoảng trống | Nguồn |
|---|---|---|
| G-30 | Câu hỏi gợi ý ở màn hình chat là nội dung nha khoa | AGT-08, CHAT-06 |
| G-31 | `GET /api/chat/threads/<thread mới>` → 404 mỗi lần mở chat | AGT-09 |
| G-32 | 1.295 schema test còn trong DB live | SEC-10 |
| G-33 | `FindDuplicateAsync` chấp nhận `tenantId = NULL` | SEC-11 |
| G-34 | Trang Models/Settings của SUPER_ADMIN trống | PLT-11, PLT-12 |
| G-35 | Trang Duyệt cấp 2 của SUPER_ADMIN hiển thị "không có voucher" thay vì báo không có phạm vi tenant | §Live |
| G-36 | Module Books (sách PDF) chỉ có API, không có UI | DOC-04 |

## Chức năng MISSING (không có trong sản phẩm)

Sổ cái / CĐPS · kê khai 01/GTGT, 05/QTT, 03/TNDN · quy tắc thuế cấu hình được · dữ liệu lương · đọc XML HĐĐT · tích hợp MISA/ERP · email/Zalo/SMS · nhiều cấp duyệt cấu hình được · cấu hình model qua UI · cài đặt nền tảng qua UI.
