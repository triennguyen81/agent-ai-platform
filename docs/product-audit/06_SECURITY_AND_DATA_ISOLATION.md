# 06 — Bảo mật & cô lập dữ liệu

Phạm vi: mục B của yêu cầu. Bằng chứng: mã nguồn, test Gateway/Agent đã chạy, 13 phép thử API chỉ GET ([evidence/api-isolation-probes.json](evidence/api-isolation-probes.json)), kiểm tra cấu hình container (không in giá trị bí mật).

## 1. Tổng kết

| Hạng mục | Đánh giá | Ghi chú |
|---|---|---|
| Tenant isolation | **PASS** | 4/4 phép thử đọc chéo tenant → 404; Agent từ chối dữ liệu tenant khác (chat C06) |
| RBAC | **PASS có ngoại lệ** | 8/9 phép thử vượt quyền cho kết quả đúng; ngoại lệ: TENANT_OWNER đọc (và theo policy là duyệt được) hàng đợi L2 |
| API authorization | **PASS** | 359/364 route yêu cầu xác thực; 5 route công khai đều có chủ đích |
| Cross-tenant | **PASS** | Như tenant isolation; storage theo tenant (`TenantStorageTests`, `DocumentExtractionTenantIsolationTests`) |
| SSRF | **PASS** | `security/ssrf.py`: kiểm tra mọi địa chỉ DNS, chặn private/loopback/metadata, kiểm từng redirect, giới hạn dung lượng khi stream (`test_ssrf_protection.py`, `test_fetch_url_ssrf.py`) |
| File upload | **PARTIAL** | Giới hạn 25 MB, kiểm tra content-type/đuôi file; **không kiểm tra magic bytes**; file lưu MinIO theo tenant |
| Secret management | **PARTIAL** | Bí mật nạp qua env; JWT key thực tế đã đặt riêng (44 ký tự); Postgres mật khẩu riêng; Redis có mật khẩu. Rủi ro: khoá mẫu trong `appsettings.json` dài 41 ký tự **vượt qua** kiểm tra ≥32 ký tự (`Program.cs:111`) nếu quên đặt env |
| Audit logging | **PARTIAL** | `audit_log` ghi mọi request ghi dữ liệu nhưng không có API/UI đọc, không bất biến; audit Agent chỉ lưu tên tool, giới hạn 5.000 |
| DLP / dữ liệu cá nhân | **FAIL (cấu hình)** | DLP tắt; chat không đi qua DLP; UI khẳng định ngược lại |

## 2. Kết quả phép thử API (2026-11-19, chỉ GET)

| Phép thử | Kỳ vọng | Thực tế |
|---|---|---|
| ADMIN (Sáu Trực) đọc chứng từ Test 123 | 404/403 | 404 ✅ |
| USER (Test 123) đọc chứng từ / khách hàng / hợp đồng Sáu Trực | 404/403 | 404 ✅ ×3 |
| USER gọi route `department_admin` (`/department/agents`) | 403 | 403 ✅ |
| USER đọc `/approval-queue` | 403 | 403 ✅ |
| TENANT_OWNER đọc chứng từ phòng ban | 403 | 403 ✅ |
| **TENANT_OWNER đọc `/approval-queue`** | không nên được | **200** ❌ (ACC-01) |
| SUPER_ADMIN đọc chứng từ tenant | 403 | 403 ✅ |
| SUPER_ADMIN đọc `/approval-queue` | 400 thiếu tenant | 400 ✅ |
| ADMIN đọc danh sách tenant nền tảng | 403 | 403 ✅ |
| Không token | 401 | 401 ✅ |
| ADMIN đọc manifest agent của tenant khác | 403/404 | 403 ✅ |

## 3. Phát hiện bảo mật

| Mã | Mức | Nội dung | Bằng chứng | Đề xuất |
|---|---|---|---|---|
| SEC-01 | HIGH | Đăng ký công khai `POST /auth/register` + trang `/signup`: ai cũng tạo được USER trong tenant `default` (3 tài khoản test đã tạo theo cách này) | `AuthEndpoints.cs:14,109-150`; `/signup` → 200 | Tắt đăng ký công khai (cờ cấu hình) hoặc chỉ qua lời mời |
| SEC-02 | HIGH | DLP **tắt** (`Dlp:Enabled=false`, không có env ghi đè, `sim_dlp_events`=0) trong khi trang DLP khẳng định "Mọi PII… được thay bằng token trước khi gửi ra LLM cloud" | `appsettings.json:57-60`; `tenant_owner-admin_dlp.png` | Bật DLP, sửa nội dung UI theo trạng thái thật |
| SEC-03 | HIGH | Chat đi thẳng web → Agent, **không qua** `ScrubbingAgentClient` của Gateway → nội dung chat không bao giờ được che PII kể cả khi bật DLP | `web/src/app/api/chat/route.ts:138` (`agent.chat` tới `AGENT_BASE`) | Che PII ở Agent hoặc định tuyến chat qua Gateway |
| SEC-04 | HIGH (production) | Postgres 5432, Prometheus 9090, Grafana 3000, Gateway 8080, Web 3030 mở trên `0.0.0.0` của host, bỏ qua Caddy (TLS + security headers) | `docker ps`; Prometheus `/-/ready` 200 không xác thực | Chỉ bind `127.0.0.1` hoặc mạng nội bộ; chỉ public Caddy 80/443 |
| SEC-05 | MEDIUM | Gateway live chạy `ASPNETCORE_ENVIRONMENT=Development` (OpenAPI bật — trả 500, Hangfire cho phép loopback) | `printenv`; `Program.cs:504-507, 548-556` | Production mode cho môi trường khách hàng |
| SEC-06 | MEDIUM | Khoá JWT mẫu `OVERRIDE_THIS_VIA_ENV_MIN_32_CHARS_______` (41 ký tự) vượt qua kiểm tra độ dài → nếu quên env, mọi người biết khoá ký token | `appsettings.json:15`, `Program.cs:111-113` | Từ chối khởi động nếu khoá trùng giá trị mẫu |
| SEC-07 | MEDIUM | `Security:AgentInternalNetworks` chưa cấu hình → header `X-AIQ-Actor` luôn bị xoá → Gateway không phân biệt được request do Agent gửi (mất lớp phòng thủ thứ hai) | `AgentActorHeaderMiddleware.cs`; không có cấu hình trong appsettings/env/compose | Cấu hình CIDR mạng của Agent |
| SEC-08 | MEDIUM | `audit_log` không bất biến, không có API/UI đọc | không có `SELECT … FROM audit_log` trong Gateway | Thêm trigger append-only + trang tra cứu theo vai trò |
| SEC-09 | MEDIUM | Upload chỉ kiểm tra content-type/đuôi file | `VoucherEndpoints.cs:1397-1403` | Kiểm tra magic bytes, quét mã độc trước khi OCR |
| SEC-10 | LOW | 1.295 schema test còn sót trong DB live | `pg_namespace` | Dọn và chạy test trên DB riêng |
| SEC-11 | LOW | `FindDuplicateAsync` chấp nhận `tenantId = NULL` (dò trùng xuyên tenant nếu caller truyền null) | `IVoucherRepository.cs:1014` | Bắt buộc tenant |
| SEC-13 | MEDIUM | 3 tài khoản demo (TENANT_OWNER, ADMIN tenant Sáu Trực; USER tenant Test 123) dùng **chung một mật khẩu yếu, dễ đoán** (phát hiện khi rà soát tài liệu không chứa mật khẩu; giá trị không ghi ở đây) | quality gate 2026-11-19 | Đổi mật khẩu mạnh, riêng từng tài khoản trước khi chia sẻ môi trường demo |
| SEC-12 | INFO | Rate limit: `auth` 10/phút, `default` 100/phút/người dùng | `Program.cs:404-430` | — |

## 4. Điểm mạnh đã xác minh

- Policy theo phòng ban (`department_admin`/`department_member`) yêu cầu user, tenant và phòng ban **đang hoạt động** (`PrincipalAuthorizationPolicy.cs`, `PrincipalAuthorizationPolicyTests`).
- Tenant của request lấy từ hồ sơ user đã lưu, không từ token hay header (`TenantContext.cs:125-137`).
- Manifest agent do Gateway ký; Agent từ chối thực thi khi không tra được quyền.
- Xác thực hoá đơn: bằng chứng do Gateway dựng, không lấy từ request; tự vô hiệu khi dữ liệu HĐ thay đổi (`TaxVerification.cs:381-393, 451-470`).
- Danh tính công ty trong Agent lấy từ server; người dùng không ghi đè được.
