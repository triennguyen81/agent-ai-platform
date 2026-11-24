# 02 — Báo cáo audit chức năng

Ngày: 2026-11-19 · Chi tiết từng chức năng: [01_SYSTEM_FUNCTIONAL_INVENTORY.md](01_SYSTEM_FUNCTIONAL_INVENTORY.md) · Mã phát hiện: [07_PRODUCT_GAP_ANALYSIS.md](07_PRODUCT_GAP_ANALYSIS.md).

## 1. Phương pháp

1. **Truy vết mã nguồn**: trang web → BFF (`web/src/app/api/**`, `web/src/lib/api.ts`) → route Gateway (trích tự động 364 route + policy: [evidence/gateway-routes.tsv](evidence/gateway-routes.tsv)) → repository/bảng DB → tool Agent.
2. **Test tự động an toàn** (không ghi DB thật): Gateway non-DB 1.211/1.211 · Agent 411 pass, 8 skip · Web 107/107. 494 integration test cần tạo DB **không chạy** (nguyên tắc Phần 0).
3. **Probe engine thuế** (gọi trực tiếp tool, không LLM): 26 ca, 12 PASS / 14 FAIL — [docs/audit/TAX_TEST_COVERAGE.md](../audit/TAX_TEST_COVERAGE.md).
4. **Kiểm tra live chỉ đọc** (§Live): đăng nhập 4 vai trò bằng tài khoản demo do người dùng cung cấp, mở 92 trang, chụp 90 ảnh, 15 lượt chat. Không bấm nút ghi dữ liệu nào.
5. **SELECT chỉ đọc** trên DB live để xác nhận cấu hình và dữ liệu demo.

## 2. Kết quả tổng

| Trạng thái | Số chức năng | Tỷ lệ |
|---|---|---|
| PASS | 74 | 43% |
| PARTIAL | 33 | 19% |
| MISSING | 11 | 6% |
| BROKEN | 7 | 4% |
| UNVERIFIED (21 có IT chưa chạy, 28 không test) | 49 | 28% |
| **Tổng** | **174** (36 module) | |

### Theo nhóm

| Nhóm | Đánh giá | Ghi chú |
|---|---|---|
| Xác thực, tenant, phòng ban, người dùng, phân quyền | **Tốt** | Policy theo phòng ban chặt; 1 lỗ hổng tự đăng ký (SEC-01) và 1 hàng đợi legacy sai phạm vi (ACC-01) |
| Agent builder, runtime, manifest, tool | **Tốt** | Manifest ký bởi Gateway, fail-closed, cap số vòng tool; PEC chỉ ở nhánh phụ |
| Chat | **Tốt có điều kiện** | Trả lời có căn cứ, chống ảo giác & prompt injection tốt (live). Câu gợi ý sai ngữ cảnh; agent gán cho USER ở tenant Test 123 thiếu tool dữ liệu |
| Knowledge base & RAG | **Chưa xác minh** | Code đầy đủ; integration test cần DB, không có lượt chat nào dùng KB trong live |
| Chứng từ & OCR | **Tốt** | Upload, OCR, phân loại, validator, xác thực HĐ có bằng chứng; dò trùng sai khoá |
| Kế toán, AR, AP, ngân hàng, chi phí | **Tốt ở lõi, thiếu ở tổng hợp** | Posting bất biến, chỉ ADMIN ghi sổ; không có sổ cái, BCTC từ sổ; vòng đời v2 **đang tắt** trên demo |
| Thuế | **Kém** | TNCN, TNDN, hạn nộp sai (xác nhận cả probe lẫn live); không có kê khai |
| Phê duyệt & HITL | **Trung bình** | Duyệt v2 tốt nhưng đang tắt; hàng đợi L2 legacy sai phạm vi |
| Dashboard, báo cáo, usage, audit | **Kém** | Dashboard chỉ là liên kết; 7 trang báo cáo/usage/audit là cùng một trang nhật ký Agent; `audit_log` không có UI |
| Giám sát | **Trung bình** | Gateway metrics ok; Agent metrics hỏng; OpenAPI 500 |
| Tích hợp | **Thiếu** | Không có MISA/ERP, không đọc XML HĐĐT, không gửi email/Zalo |

## 3. Chức năng BROKEN

| ID | Chức năng | Lỗi | Bằng chứng |
|---|---|---|---|
| TAX-04 | Máy tính TNCN | Dùng giảm trừ 11tr/4,4tr + biểu 7 bậc cho kỳ 2026 | probe PIT-01..07; live C04 |
| TAX-05 | Máy tính TNDN | Mặc định 20%, không xét 15%/17% | probe CIT-01..03 |
| TAX-06 | Hạn nộp tờ khai | Hạn quý ngày 30 (sai Q2/Q3/Q4), 02/QTT-TNCN sai, không dời ngày nghỉ | probe DL-03..06; live C07 |
| RPT-02 | Trang Báo cáo tenant/phòng ban | Bí danh của trang nhật ký Agent | `web/src/app/(app)/tenant/reports/page.tsx` (1 dòng `export … from "../../admin/audit/page"`); ảnh `tenant_owner-tenant_reports.png` |
| USG-02 | Trang Usage tenant/nền tảng | Như trên | `tenant/usage/page.tsx`, `system/usage/page.tsx` |
| MON-03 | Metrics Agent | Prometheus target `aiq-agent` down — 404 `/metrics` | Prometheus `/api/v1/targets` |
| MON-06 | OpenAPI dev | `/openapi/v1.json` → 500 | curl |

## 4. Đặc biệt kiểm tra (Phần 2 của yêu cầu)

- **A. Business logic, C. Accounting safety**: [04_ACCOUNTING_WORKFLOW_AUDIT.md](04_ACCOUNTING_WORKFLOW_AUDIT.md)
- **B. Security**: [06_SECURITY_AND_DATA_ISOLATION.md](06_SECURITY_AND_DATA_ISOLATION.md)
- **D. Tax governance**: [03_ROLE_PERMISSION_MATRIX.md §4](03_ROLE_PERMISSION_MATRIX.md)
- **E. Agent**: [05_AI_AGENT_AUDIT.md](05_AI_AGENT_AUDIT.md)

---

## §Live — Kiểm tra trên môi trường đang chạy (2026-11-19)

Môi trường: `localhost:3030` (web) / `localhost:8080` (gateway), container build từ HEAD `b0d7aaf` + thay đổi đăng nhập → chat. Playwright Chromium 1440×900, giãn 6 giây giữa các trang. Kết quả thô: [evidence/ui-walk-results.json](evidence/ui-walk-results.json); ảnh: [screenshots/](screenshots/).

### Tài khoản (mật khẩu do người dùng cung cấp, không lưu trong tài liệu)

| Vai trò | Tài khoản | Tenant / phòng ban | Sau đăng nhập |
|---|---|---|---|
| SUPER_ADMIN | `demo@aiq.local` | nền tảng | `/dashboard` |
| TENANT_OWNER | `sautruc@sautruc.com` | cty sáu trực | `/dashboard` |
| ADMIN | `admin1@sautruc.com` | cty sáu trực / Default Department | `/chat?agent=…` (Trợ lý kế toán) |
| USER | `aaaa@123a.xx` | Test 123 / Default Department | `/chat?agent=…` (**Onboarding Smoke**) |

### Duyệt trang

| Vai trò | Số trang | Mở được | Bị chặn đúng thiết kế | Lỗi thật |
|---|---|---|---|---|
| SUPER_ADMIN | 17 | 14 | 3 (`/chat`, `/vouchers`, `/admin/tenant-status`) | 0 (trang duyệt cấp 2 hiển thị rỗng thay vì báo thiếu tenant) |
| TENANT_OWNER | 15 | 13 | 2 (`/chat`, `/vouchers`) | 1 tài nguyên 404 ở `/admin/dlp` (console) |
| ADMIN | 42 | 41 | 1 (`/admin/health`) | 1: `GET /api/chat/threads/<thread mới>` → 404 mỗi lần mở chat |
| USER | 18 | 14 | 4 (`/dashboard`, `/department/agents`, `/admin/approval-queue`, `/admin/audit`) | 1: như ADMIN |

Từ khoá "500" xuất hiện ở 20 lượt mở trang; đó là **số tiền** (ví dụ "500.000"), không phải lỗi — đã đối chiếu ảnh `admin-vouchers.png`.

Quan sát thêm:
- TENANT_OWNER **mở được** `/admin/approval-queue` (khớp ACC-01).
- Dashboard 3 vai trò quản trị chỉ có 4–5 thẻ liên kết (`super_admin-dashboard.png`).
- Chat: 4 câu gợi ý về **nha khoa** (`admin-root.png`).
- DLP: 0 sự kiện, trang tự ghi "có thể DLP đang disable" (`tenant_owner-admin_dlp.png`).
- Dữ liệu demo lẫn bản ghi `LIVE-TEST` và dữ liệu thử ("LIVE-TEST Cty Kiểm Thử HĐ", "LIVE-TEST Nguyễn Văn Công Tác") — `admin-vouchers.png`, `admin-customers_id.png`.
- USER Test 123 mặc định vào agent "Onboarding Smoke" (agent thử nghiệm ở trạng thái production, đứng đầu theo tên).

### Chat (15 lượt, chỉ đọc) — chi tiết [05 §3](05_AI_AGENT_AUDIT.md)

| Kết quả | Lượt |
|---|---|
| Đúng, có căn cứ tool | C01, C02 |
| Từ chối bịa nguồn | C03, C13, C14 |
| Từ chối ghi sổ / duyệt / chi tiền | C05, C15 |
| Từ chối truy cập tenant khác | C06 |
| **Sai do engine thuế** | C04 (TNCN), C07 (hạn nộp) |
| Không dùng được (agent test) | C08–C12 (Onboarding Smoke trả `ONBOARDING_SMOKE_OK`) |

Sau 15 lượt: 0 đề xuất bút toán mới, 0 chứng từ thay đổi (SELECT kiểm tra).
