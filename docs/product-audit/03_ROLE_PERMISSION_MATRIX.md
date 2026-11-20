# 03 — Ma trận vai trò & phân quyền

Nguồn: policy thực tế trong [Program.cs:366-400](../../gateway/src/Aiq.Gateway/Program.cs#L366-L400), [PrincipalAuthorizationPolicy.cs](../../gateway/src/Aiq.Gateway/Governance/PrincipalAuthorizationPolicy.cs), bảng route [evidence/gateway-routes.tsv](evidence/gateway-routes.tsv), điều kiện trang web [evidence/web-pages.tsv](evidence/web-pages.tsv). Nguyên tắc sản phẩm: **ADMIN Department Authority Invariant** ([CLAUDE.md](../../CLAUDE.md), [ACL_V2_RESOURCE_VISIBILITY_RULES.md §15](../ACL_V2_RESOURCE_VISIBILITY_RULES.md)).

## 1. Bốn vai trò thực tế

| Vai trò | Thuộc tenant | Thuộc phòng ban | Policy chính | Phạm vi |
|---|---|---|---|---|
| SUPER_ADMIN (SA) | Không | Không | `platform_admin` | Quản trị nền tảng SaaS: tenant, agent mẫu, skill, capability, tri thức nền tảng |
| TENANT_OWNER (TO) | Có | Không | `tenant_governance` | Quản trị tenant: phòng ban, ADMIN phòng ban, người dùng, hồ sơ công ty, DLP |
| ADMIN (AD) | Có | Có | `department_admin` (+ `department_member`) | Toàn bộ nghiệp vụ của phòng ban mình, kể cả với bản ghi do chính mình tạo |
| USER (US) | Có | Có | `department_member` | Dùng agent được gán, tạo và nộp chứng từ, đề xuất; không quyết định nghiệp vụ kế toán |

Không có vai trò Kế toán trưởng / Kế toán viên / Người được uỷ quyền thuế / Kiểm toán viên riêng. "Kế toán trưởng" trong nghiệp vụ = ADMIN phòng kế toán.

Mọi policy mới đều yêu cầu user **đang hoạt động**; TO/AD/US còn yêu cầu tenant (và phòng ban với AD/US) đang hoạt động.

## 2. Phân bố 364 route Gateway theo policy

| Policy | Số route | Ai qua được |
|---|---|---|
| `department_admin` | 109 | AD |
| `department_member` | 78 | AD, US |
| `department_member` + kiểm tra `department_admin` | 27 | AD |
| `platform_admin` | 73 | SA |
| `tenant_governance` | 20 | TO |
| `admin` (**legacy, theo role**) | 24 | SA, TO, AD |
| `user` (legacy) | 6 | SA, TO, AD, US |
| `authenticated` | 13 | mọi user đang hoạt động |
| `uploader` / `authenticated,uploader` | 3 | SA, AD (role `uploader` không còn dùng) |
| `resource_or_tool` | 3 | token tool của Agent hoặc vai trò phù hợp |
| `catalog_reader` | 3 | SA, AD |
| anonymous | 5 | công khai: `/auth/login`, `/auth/register`, `/auth/external-link` (shared secret), `/healthz/*` |

225 route ghi dữ liệu, 139 route đọc. Ngoài policy route, nhiều handler kiểm tra thêm, ví dụ hợp đồng (`ContractEndpoints.cs:55-61`: xoá, duyệt, bàn giao chỉ ADMIN).

## 3. Ma trận chức năng × vai trò

Ký hiệu: ✅ được phép · 👁 chỉ xem · 📝 đề xuất (không quyết định) · ❌ không được · ⚠️ được phép nhưng **không nên** (lỗ hổng, xem ghi chú).

| Nhóm chức năng | SA | TO | AD | US | Agent (token của người gọi) |
|---|---|---|---|---|---|
| Tạo tenant, quota, agent/skill/capability/tri thức nền tảng | ✅ | ❌ | ❌ | ❌ | ❌ |
| Phòng ban, ADMIN phòng ban, hồ sơ công ty, DLP summary | ❌ | ✅ | ❌ | ❌ | ❌ |
| Người dùng (tạo, khoá, đặt lại mật khẩu) | ❌ | ✅ toàn tenant | ✅ phòng mình | ❌ | ❌ |
| Agent phòng ban: tạo, version, publish, gán skill/KB/tool, gán người dùng | ❌ | ❌ | ✅ | ❌ | ❌ |
| Knowledge base phòng ban | ❌ | ❌ | ✅ | 👁 qua agent | 👁 chỉ KB trong manifest |
| Chat với agent | ❌ (không có agent) | ❌ | ✅ agent của phòng | ✅ agent được gán | — |
| Tải chứng từ, sửa trường trích xuất, trích xuất lại | ❌ | ❌ | ✅ | ✅ | ✅ (`save_voucher`, `apply-extraction`) |
| Nộp chứng từ (v2 submit) | ❌ | ❌ | ✅ | ✅ | ❌ |
| Duyệt / trả lại / từ chối chứng từ (v2) | ❌ | ❌ | ✅ | ❌ | ❌ |
| Đề xuất bút toán | ❌ | ❌ | ✅ | ✅ | 📝 (`save_journal_suggestions`, chỉ cặp TK cho phép) |
| Quyết định bút toán, ghi sổ, đảo bút toán | ❌ | ❌ | ✅ | ❌ | ❌ không có tool |
| Ghi nhận công nợ (apply-invoice / apply-expense / apply-receipt) | ❌ | ❌ | ✅ | 📝 (receipt-match propose) | ❌ không có tool |
| Xác thực hoá đơn với cơ quan thuế | ❌ | ❌ | ✅ (có bằng chứng) | 📝 | 📝 (`propose_tax_verification`) |
| Kết luận hoá đơn không hợp lệ | ❌ | ❌ | ✅ | ❌ | ❌ (Gateway từ chối `actorType=agent`) |
| Đề nghị thanh toán: tạo, thêm file | ❌ | ❌ | ✅ | ✅ | ❌ |
| Đề nghị thanh toán: duyệt phòng ban, duyệt kế toán, chi tiền | ❌ | ❌ | ✅ (kể cả của chính mình) | ❌ | ❌ (test chặn) |
| Đóng / mở kỳ kế toán | ❌ | ❌ | ✅ (API, không có UI) | ❌ | ❌ |
| Hợp đồng: tải, trích xuất, xử lý | ❌ | ❌ | ✅ | ✅ (của mình) | ✅ (`save_contract`) |
| Hợp đồng: duyệt, bàn giao, xoá | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Hàng đợi duyệt L2 legacy `/approval-queue`** | ⚠️ policy cho qua (thiếu tenant scope → 400) | ⚠️ **duyệt được** | ⚠️ duyệt được cả **phòng khác** | ❌ | ❌ |
| Nhật ký Agent `/admin/audit` | ✅ toàn nền tảng | ✅ tenant | ✅ phòng mình | ❌ | ❌ |
| Bảng `audit_log` Gateway | ❌ không có API | ❌ | ❌ | ❌ | ❌ |
| Thông báo của mình | ✅ | ✅ | ✅ | ✅ | ❌ |
| Tự đăng ký tài khoản | — | — | — | ⚠️ ai cũng tạo được USER trong tenant `default` | — |

## 4. Đối chiếu yêu cầu Tax Governance (mục D)

| Yêu cầu | Kết quả | Bằng chứng |
|---|---|---|
| SA chỉ quản trị nền tảng, không duyệt nghiệp vụ tenant | **Đạt** với mọi route nghiệp vụ mới (`department_*` đòi `DepartmentId`, SA không có). Trường hợp legacy `/approval-queue`: SA qua policy nhưng không có tenant scope → 400 | `PrincipalAuthorizationPolicy.cs:14-27`; `ApprovalEndpoints.cs:45-47` |
| TO không duyệt nghiệp vụ kế toán | **Không đạt** ở `/approval-queue/{id}/review` (policy `admin`, chỉ kiểm tra tenant) | `ApprovalEndpoints.cs:23-27, 74-87` |
| ADMIN quản lý nghiệp vụ, duyệt trong phạm vi phòng ban | **Đạt** cho chứng từ v2, đề nghị thanh toán, ghi sổ, xác thực HĐ; **không đạt** ở `/approval-queue` (tenant-wide) | `department_admin` + `IsSameDepartment` trong repository |
| ADMIN duyệt quy tắc thuế | **Không áp dụng**: hệ thống không có quy tắc thuế cấu hình được (đã bỏ cùng Phase 0) | — |
| USER không tự duyệt nghiệp vụ kế toán | **Đạt** | các route quyết định đều `department_admin` |

## 5. Ghi chú về phân tách nhiệm vụ

Theo quyết định sản phẩm, ADMIN cùng phòng ban **được** thực hiện trọn quy trình (tạo → duyệt phòng ban → duyệt kế toán → ghi nhận → chi tiền), kể cả trên bản ghi của mình (`AdminDepartmentAuthorityInvariantTests`, `ExpenseClaimSameAdminLifecycleTests`). Audit không ghi nhận đây là lỗi. Doanh nghiệp cần tách người lập / người duyệt thì phải phân vai bằng cách chỉ giao vai trò ADMIN cho kế toán trưởng — hệ thống không tự bắt buộc.
