# 15 — Lộ trình đề xuất

Mã vấn đề: [07_PRODUCT_GAP_ANALYSIS.md](07_PRODUCT_GAP_ANALYSIS.md). Chi tiết thuế: [docs/audit/TAX_REMEDIATION_ROADMAP.md](../audit/TAX_REMEDIATION_ROADMAP.md).
Nguyên tắc giữ nguyên: **ADMIN Department Authority Invariant** ([CLAUDE.md](../../CLAUDE.md)) — không thêm phân tách nhiệm vụ cho ADMIN nếu không có quyết định sản phẩm riêng.

Mỗi hạng mục cần được phê duyệt trước khi bắt đầu. Ước lượng công sức: S (≤ 1 ngày), M (2–5 ngày), L (> 1 tuần).

## P0 — Bắt buộc trước demo

| # | Việc | Mã | Loại | Công sức |
|---|---|---|---|---|
| P0-1 | Tạo USER demo trong tenant Sáu Trực, gán "Trợ lý kế toán" | — | Dữ liệu | S |
| P0-2 | Tắt / thu hồi agent thử nghiệm "Onboarding Smoke", "zzz" khỏi người dùng demo | G-22 | Dữ liệu | S |
| P0-3 | Dọn / đổi tên dữ liệu `LIVE-TEST…`; sửa hồ sơ công ty tenant Test 123 nếu dùng | G-28 | Dữ liệu | S |
| P0-4 | Chuẩn bị hoá đơn và đề nghị hoàn ứng **mới** (số chưa tồn tại) + bản dự phòng | G-10 | Dữ liệu | S |
| P0-5 | Quyết định bật `voucher_flow_v2` cho tenant demo; nếu bật thì tổng duyệt nộp/duyệt/trả lại/từ chối | G-13 | Cấu hình | S–M |
| P0-6 | Thay 4 câu gợi ý nha khoa trên màn hình chat bằng câu hỏi kế toán | G-30 | Code nhỏ | S |
| P0-7 | Gắn nhãn "thử nghiệm – chưa dùng cho kê khai" trên `/accounting/tax-calc` (TNCN/TNDN) và `/accounting/deadline`, hoặc ẩn khỏi menu demo | G-01, G-02, G-09 | Code nhỏ | S |
| P0-8 | Sửa nội dung trang DLP theo trạng thái cấu hình thật (đang tắt) | G-06 | Code nhỏ | S |
| P0-9 | Chạy integration test DB (`ExpenseClaim*`, `VoucherLifecycle*`, `TaxVerification*`, `CashSettlement*`) trên DB riêng | — | Kiểm thử | S |
| P0-10 | Tổng duyệt toàn bộ kịch bản [12](12_DEMO_SCRIPT.md) trên đúng môi trường; xác nhận hạn mức mô hình AI | — | Kiểm thử | S |

## P1 — Trước nghiệm thu (UAT)

| # | Việc | Mã | Công sức |
|---|---|---|---|
| P1-1 | Chuyên gia thuế xác nhận các điểm LEGAL_REVIEW_REQUIRED | G-01, G-02, G-03, G-09 | — |
| P1-2 | Quy tắc thuế có version theo kỳ (mở rộng `vat_rates`); engine TNCN/TNDN ở Gateway, `decimal`, làm tròn VND; tool Agent gọi engine | G-01, G-02, G-27 | L |
| P1-3 | Quy tắc khấu trừ VAT (20tr/5tr theo ngày HĐ, cộng gộp, chứng từ thanh toán thực tế, MST người mua, trạng thái xác thực) | G-03 | M |
| P1-4 | Lịch nghĩa vụ kê khai một nguồn (ngày cuối tháng, năm tài chính, ngày nghỉ); bỏ logic trùng ở web | G-09 | M |
| P1-5 | Hàng đợi duyệt L2: chuyển sang `department_admin` + kiểm tra phòng ban của chứng từ (TENANT_OWNER không duyệt) | G-04 | S |
| P1-6 | Khoá dò trùng hoá đơn đúng (MST + mẫu số + ký hiệu + số chuẩn hoá); trùng → chờ duyệt, không tự từ chối | G-10, G-33 | M |
| P1-7 | Hồ sơ tính toán AI bất biến (đầu vào, đầu ra, version quy tắc, căn cứ, người gọi) | G-12 | M |
| P1-8 | Dashboard số liệu theo vai trò; trang Reports/Usage thật (dùng API báo cáo tuần, usage đã có) | G-14 | M |
| P1-9 | Màn hình tra cứu `audit_log` theo phạm vi vai trò; append-only | G-19 | M |
| P1-10 | Cập nhật skill `tax-calculation` và nội dung pháp lý trong prompt | G-24 | S |
| P1-11 | Chuẩn hoá cấu hình agent cho USER (tool dữ liệu đúng vai trò) | G-23 | S |
| P1-12 | Agent `/metrics` cho Prometheus; sửa OpenAPI | G-21, G-16 | S |
| P1-13 | Đưa toàn bộ 494 integration test vào quy trình kiểm thử định kỳ trên DB riêng | — | S |

## P2 — Trước production

| # | Việc | Mã | Công sức |
|---|---|---|---|
| P2-1 | Tắt đăng ký công khai (chỉ qua lời mời) | G-05 | S |
| P2-2 | Bật DLP; che PII cho cả nội dung chat; kiểm định | G-06, G-07 | M |
| P2-3 | Chỉ public Caddy 80/443; Postgres/Prometheus/Grafana/Gateway/Web chỉ mạng nội bộ | G-08 | S |
| P2-4 | `ASPNETCORE_ENVIRONMENT=Production`; từ chối khởi động khi JWT key = giá trị mẫu | G-16, G-17 | S |
| P2-5 | Cấu hình `Security:AgentInternalNetworks`; lớp chặn token do Agent dùng trên route ghi sổ/duyệt | G-18 | M |
| P2-6 | Kiểm tra magic bytes + quét mã độc file tải lên | G-20 | M |
| P2-7 | Sổ cái, sổ chi tiết, CĐPS từ bút toán đã ghi sổ; BCTC từ sổ | G-11 | L |
| P2-8 | Màn hình đóng/mở kỳ kế toán | G-26 | S |
| P2-9 | Bước Critic / đối chiếu kết quả tool trên luồng chat chính | G-25 | M |
| P2-10 | Dọn 1.295 schema test; tách DB test khỏi DB vận hành | G-32 | S |
| P2-11 | Sao lưu/khôi phục, kiểm thử tải, kiểm thử xâm nhập độc lập | — | M–L |

## P3 — Sau triển khai

| # | Việc | Mã |
|---|---|---|
| P3-1 | Tổng hợp VAT theo kỳ, tờ khai 01/GTGT + XML, theo dõi nộp, tờ khai bổ sung | G-15 |
| P3-2 | Module tiền lương + TNCN (khấu trừ, quyết toán, chứng từ khấu trừ, 05/QTT) | G-15 |
| P3-3 | TNDN: tạm nộp quý, chuyển lỗ, ưu đãi, 03/TNDN | G-15 |
| P3-4 | Tích hợp MISA/ERP; đọc XML hoá đơn điện tử; liên kết HĐ gốc ↔ điều chỉnh/thay thế | G-29 |
| P3-5 | Thông báo email/Zalo; quy trình duyệt nhiều cấp cấu hình được | G-29 |
| P3-6 | Trang Models/Settings cho SUPER_ADMIN; UI cho Books | G-34, G-36 |
