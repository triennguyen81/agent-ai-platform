# 09 — Hướng dẫn quản trị AIQ

Dành cho: SUPER_ADMIN (vận hành nền tảng), TENANT_OWNER (quản trị công ty), ADMIN phòng ban (trưởng phòng / kế toán trưởng).
Chỉ mô tả chức năng đã kiểm tra hoạt động hoặc có triển khai đầy đủ; mục chưa xác minh trên môi trường thật ghi rõ "chưa kiểm tra live". Ảnh: [screenshots/](screenshots/).

---

## §2. SUPER_ADMIN — Quản trị nền tảng

- **Mục đích:** quản lý các công ty (tenant) dùng AIQ và tài nguyên AI dùng chung.
- **Phạm vi:** SUPER_ADMIN **không** xem hay xử lý chứng từ, chat, công nợ của công ty (đã kiểm tra: `/vouchers`, `/chat` → "Không có quyền truy cập").
- **Sau đăng nhập:** Dashboard với 4 thẻ: Tenants, Agent Builder, Tenant Owners, Platform Audit (`super_admin-dashboard.png`).

| Việc | Menu | Các bước | Ghi chú |
|---|---|---|---|
| Xem / tạo công ty | Platform / System → **Tenants** (`/admin/tenants`) | Xem danh sách và số liệu tổng quan từng tenant; tạo tenant mới kèm tài khoản TENANT_OWNER (onboard) | Tạo mới chưa kiểm tra live |
| Hạn mức công ty | Tenants → chọn tenant | Xem / sửa quota | |
| Agent mẫu nền tảng | Global AI Resources → **Agent Templates** (`/admin/agent-builder`) | Tạo agent, soạn version, Validate, Publish, gán cho tenant, clone sang phòng ban | Thao tác ghi chưa kiểm tra live |
| Skills / Capabilities / Knowledge / Tools / Workflows dùng chung | Global AI Resources | Quản lý danh mục dùng chung cho các tenant | |
| Nhật ký hoạt động AI toàn nền tảng | Operations → **Platform Audit** | Xem các lượt chat/tool của Agent, thời gian, chi phí | Chỉ nhật ký Agent; xem §8 |

**Giới hạn hiện tại:** Models / Providers và Settings là trang thông tin trống (cấu hình qua biến môi trường); **Usage / Billing** hiển thị cùng nội dung với Platform Audit (chưa có báo cáo usage riêng).

---

## §3. TENANT_OWNER — Quản trị công ty

- **Mục đích:** dựng cơ cấu công ty trên AIQ.
- **Phạm vi:** TENANT_OWNER **không** làm nghiệp vụ kế toán (đã kiểm tra: `/vouchers`, `/chat` → "Không có quyền truy cập").

| Việc | Menu | Các bước |
|---|---|---|
| Phòng ban | Tenant Management → **Departments** (`/admin/tenant-departments`) | Tạo phòng ban (mã, tên); sửa; khoá phòng ban |
| Người dùng & ADMIN phòng ban | Tenant Management → **Users** (`/admin/tenant-users`) | Tạo người dùng, chọn phòng ban và vai trò (ADMIN / USER); khoá / mở khoá; đặt lại mật khẩu |
| Hồ sơ công ty | Dashboard → **Hồ sơ doanh nghiệp** / `/tenant/company-profile` | Nhập **tên pháp lý, MST, địa chỉ, tên viết tắt** — bắt buộc chính xác: trợ lý AI dùng hồ sơ này làm danh tính công ty và validator dùng MST để kiểm tra hoá đơn đầu vào/đầu ra |
| Hạn mức công ty | `/admin/tenant-status` | Xem quota |
| Thống kê DLP | `/admin/dlp` | Xem số lượt che dữ liệu cá nhân |
| Nhật ký AI của công ty | Tenant Operations → **Audit** | Xem hoạt động Agent trong công ty |

**Lỗi thường gặp:** hoá đơn bị cảnh báo "Chưa cấu hình MST công ty" → nhập MST ở hồ sơ công ty.

**Giới hạn hiện tại**
- Trang DLP hiển thị 0 sự kiện vì **DLP đang tắt** trên môi trường này; nội dung "mọi PII được che" trên trang chưa đúng với cấu hình hiện tại.
- **Usage** và **Reports** hiển thị cùng nội dung với **Audit** (chưa có báo cáo riêng).
- TENANT_OWNER hiện **mở được** trang Duyệt cấp 2 (`/admin/approval-queue`). Theo nguyên tắc sản phẩm, TENANT_OWNER không duyệt nghiệp vụ kế toán — **không dùng** chức năng này bằng tài khoản TENANT_OWNER (lỗi đã ghi nhận G-04).

---

## §4. Quản lý người dùng

| Việc | Ai làm | Đường dẫn | Kết quả |
|---|---|---|---|
| Tạo người dùng | TENANT_OWNER (mọi phòng), ADMIN (phòng mình) | `/admin/tenant-users` (ADMIN: Department → **Members**) | Người dùng đăng nhập được, thuộc đúng phòng ban |
| Đổi vai trò USER ↔ ADMIN | TENANT_OWNER | `/admin/tenant-users` | |
| Khoá / mở khoá | TENANT_OWNER, ADMIN | như trên | Người bị khoá không đăng nhập và không gọi được API |
| Đặt lại mật khẩu | TENANT_OWNER, ADMIN | như trên | Người dùng phải đổi mật khẩu lần đầu |

**Lưu ý phân quyền:** chỉ có 4 vai trò (SUPER_ADMIN, TENANT_OWNER, ADMIN, USER). "Kế toán trưởng" = ADMIN của phòng kế toán. ADMIN được làm trọn quy trình kế toán của phòng, kể cả với chứng từ do chính mình lập — nếu công ty cần tách người lập / người duyệt, chỉ cấp vai trò ADMIN cho người duyệt.

**Giới hạn hiện tại:** trang `/signup` cho phép **bất kỳ ai tự đăng ký** tài khoản USER vào công ty mặc định (`default`) — cần tắt trước khi đưa ra Internet (G-05).

---

## §5. Quản lý trợ lý AI (Agent) — ADMIN phòng ban

- **Đường dẫn:** menu **WORKSPACE** → **Agents** (`/department/agents`) → chọn agent (`admin-department_agents_id.png`).
- **Các tab:** Identity · Runtime · Nghiệp vụ · Workflow / Delegates · Xử lý chứng từ · Knowledge · Nhân sự · Version History · Test.

**Quy trình chuẩn**
1. **Identity:** tên, mô tả, mức hành động (Action Tier) → **Lưu identity**.
2. **Runtime:** cấu hình nhà cung cấp và mô hình AI cho version (lưu theo version; số vòng tool bị giới hạn tối đa 12 ở phía hệ thống).
3. **Nghiệp vụ:** gắn nghiệp vụ (capability) — quyết định menu Kế toán / Chứng từ / Hợp đồng / Khách hàng / Nhà cung cấp / Chi phí hiện cho người dùng và trợ lý nào xử lý.
4. **Xử lý chứng từ:** loại chứng từ và trường cần trích xuất; có nút thử nghiệm.
5. **Knowledge:** gắn kho tri thức (tạo ở WORKSPACE → **Knowledge**: nhập tên → **Tạo KB** → chọn KB → **Thêm tài liệu**).
6. **Nhân sự:** gán USER được dùng trợ lý (Department → **Agent Assignments** chuyển về đây).
7. Mọi thay đổi tạo **bản nháp** → **Lưu draft** → **Validate** → **Publish vN**. Version History lưu các bản đã publish.
8. **Kill switch → Disable:** dừng trợ lý ngay với mọi người dùng trong phòng.

**Lưu ý quan trọng:** trợ lý chỉ dùng được dữ liệu nghiệp vụ khi được cấp tool tương ứng (danh sách chứng từ, công nợ…). Trợ lý không có tool sẽ trả lời "không có công cụ truy cập dữ liệu".

**Giới hạn hiện tại**
- Các thao tác ghi (tạo, publish, gán) chưa kiểm tra live trong đợt audit (có test tích hợp chưa chạy).
- "Workflow" là chỉ dẫn từng bước đưa vào trợ lý, **không phải** công cụ tự động chạy quy trình.
- Môi trường demo có trợ lý thử nghiệm ("Onboarding Smoke", "zzz") đang ở trạng thái production — nên tắt trước khi demo.

---

## §6. Phê duyệt

| Loại | Ai duyệt | Đường dẫn | Trạng thái |
|---|---|---|---|
| Duyệt chứng từ (nộp → duyệt / trả lại / từ chối có lý do) | ADMIN | Chi tiết chứng từ | **Cần bật quy trình chứng từ v2 cho công ty** — hiện tắt trên mọi tenant demo |
| Đề nghị thanh toán: duyệt phòng ban → duyệt kế toán → chi tiền | ADMIN | Chi tiết đề nghị (`/vouchers/<id>`) | Hoạt động (xem [10 §5](10_ACCOUNTING_USER_MANUAL.md)) |
| Hợp đồng: duyệt, bàn giao | ADMIN | Chi tiết hợp đồng | Hoạt động |
| Xác thực hoá đơn với cơ quan thuế | ADMIN (có bằng chứng) | Chi tiết hoá đơn | Hoạt động (xem [10 §1](10_ACCOUNTING_USER_MANUAL.md)) |
| Cổng HITL của trợ lý | ADMIN | Department → **Review / HITL**; `/admin/hitl-queue` | Trang mở được; quyết định chưa kiểm tra live |
| Duyệt cấp 2 (chứng từ rủi ro cao) | ADMIN | `/admin/approval-queue` | Cơ chế cũ: **không giới hạn theo phòng ban**; duyệt chỉ đổi trạng thái, kế toán vẫn phải ghi nhận trên chứng từ |

---

## §7. Dashboard

`/dashboard` hiện là trang **lối tắt** theo vai trò (4–5 thẻ liên kết), **chưa có số liệu**. Số liệu nghiệp vụ xem ở: Chứng từ (bộ lọc trạng thái), Khách hàng / Nhà cung cấp (công nợ), **Báo cáo trạng thái chứng từ** (`/admin/voucher-status`).

---

## §8. Nhật ký (Audit Log)

- **Đường dẫn:** SUPER_ADMIN: Platform Audit; TENANT_OWNER: Tenant Operations → Audit; ADMIN: Department → Audit.
- **Nội dung:** mỗi lượt chat / tool của trợ lý AI: thời điểm, người dùng, agent, tool đã dùng, thời gian chạy, token, chi phí ước tính, thành công/thất bại.
- **Phạm vi:** SUPER_ADMIN toàn nền tảng; TENANT_OWNER công ty mình; ADMIN phòng mình.
- **Lịch sử nghiệp vụ** của từng chứng từ (tạo, sửa, xác thực HĐ, duyệt) xem trong trang chi tiết chứng từ.

**Giới hạn hiện tại:** nhật ký chỉ lưu tên tool, không lưu nội dung câu hỏi/kết quả, và chỉ giữ 5.000 bản ghi gần nhất. Nhật ký thao tác của người dùng trên hệ thống (bảng `audit_log`) **chưa có màn hình xem**.
