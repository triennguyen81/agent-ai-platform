# ACL V2 – Resource Visibility & Workspace Rules

## 1. Mục tiêu

Chuẩn hóa cách hiển thị và phân quyền tài nguyên trong hệ thống theo nguyên tắc:

- Tài nguyên có sẵn của hệ thống được dùng chung cho tất cả phòng ban.
- Tài nguyên do một phòng ban tạo chỉ thuộc và chỉ hiển thị trong phòng ban đó.
- ADMIN nhìn thấy một Workspace duy nhất, không tách menu Global/Department.
- Phân biệt Global và Department bằng quyền và badge trong cùng một danh sách, không bằng menu riêng.

---

## 2. Role model

```text
SUPER_ADMIN
→ Quản lý Platform / System / Tenant

TENANT_OWNER = Giám đốc công ty
→ Quản lý cơ cấu Tenant
→ Departments / ADMIN / USER
→ Không vận hành tài nguyên AI hằng ngày

ADMIN = Trưởng phòng
→ Quản lý toàn bộ AI Workspace của phòng mình
→ Quản lý USER của phòng mình

USER = Nhân viên
→ Sử dụng Agent/Nghiệp vụ được assign
→ Không quản lý Workspace
```

---

## 3. Hai scope tài nguyên

### 3.1 GLOBAL / SYSTEM RESOURCE

Do `SUPER_ADMIN` tạo và quản lý.

Ví dụ:

- Global Tools
- Global Skills
- Global Workflows / Workflow Templates
- Global Knowledge / Knowledge Templates
- Global Agent Templates
- Global Capability Templates

Quyền:

```text
SUPER_ADMIN
→ CREATE / READ / UPDATE / DISABLE / MANAGE

ADMIN
→ READ / USE / ATTACH
→ KHÔNG EDIT
→ KHÔNG DELETE
→ KHÔNG thay đổi nội dung gốc

USER
→ Không quản lý trực tiếp
→ Chỉ sử dụng gián tiếp thông qua Agent được assign
```

Global resource có cấu trúc scope:

```text
scope = global
tenant_id = NULL
department_id = NULL
```

---

### 3.2 DEPARTMENT RESOURCE

Do `ADMIN / Trưởng phòng` tạo cho chính phòng ban của mình.

Ví dụ:

- Department Agents
- Department Capabilities / Nghiệp vụ
- Department Workflows
- Department Skills
- Department Knowledge

Quyền:

```text
ADMIN cùng Department
→ CREATE / READ / UPDATE / DISABLE / USE / ATTACH

USER cùng Department
→ Chỉ sử dụng thông qua Agent được assign

ADMIN Department khác
→ KHÔNG THẤY

USER Department khác
→ KHÔNG THẤY

Tenant khác
→ KHÔNG THẤY
```

Department resource có cấu trúc:

```text
scope = department
tenant_id = authenticated_principal.tenant_id
department_id = authenticated_principal.department_id
created_by = authenticated_principal.user_id
```

Browser không được quyết định `tenant_id`, `department_id` hoặc ownership.

---

## 4. Workspace của ADMIN

ADMIN chỉ thấy **một Workspace duy nhất**:

```text
AI WORKSPACE
├── Agents
├── Nghiệp vụ
├── Workflows
├── Skills
├── Knowledge
└── Tools
```

Không tạo menu riêng:

```text
Global Skills
Department Skills
Global Workflows
Department Workflows
```

vì sẽ làm UX phức tạp.

Thay vào đó, trong cùng một danh sách, backend trả về:

```text
Global resources
+
Resources của chính Department
```

Ví dụ trang Skills:

```text
Vietnamese Accounting       🌐 Hệ thống
Contract Analysis            🌐 Hệ thống
Quy trình chốt sổ ABC        🏢 Phòng Kế toán
Kiểm tra công nợ nội bộ      🏢 Phòng Kế toán
```

Quyền UI:

```text
🌐 Hệ thống
→ Xem
→ Dùng
→ Attach
→ Không có Edit/Delete

🏢 Phòng ban
→ Xem
→ Dùng
→ Attach
→ Edit/Disable/Delete theo policy
```

---

## 5. Visibility query rule

ADMIN được nhìn thấy một resource khi:

```text
scope = global

OR

(
    scope = department
    AND tenant_id = principal.tenant_id
    AND department_id = principal.department_id
)
```

ADMIN không bao giờ được nhận resource Department của phòng khác trong API response.

---

## 6. Agent Builder

ADMIN tạo Agent cho phòng mình và được chọn resource từ một danh sách thống nhất.

```text
Create Department Agent
        ↓
Select Model
        ↓
Select Tools
        ↓
Select Workflow
        ↓
Select Skills
        ↓
Select Knowledge
        ↓
Select Capabilities
        ↓
Validate / Test
        ↓
Publish
        ↓
Assign USER
```

Trong picker:

```text
Tools
☑ OCR Invoice                 🌐 Hệ thống
☑ Send Email                  🌐 Hệ thống

Skills
☑ Vietnamese Accounting      🌐 Hệ thống
☑ Quy trình kế toán ABC       🏢 Phòng Kế toán

Workflows
○ Invoice Processing          🌐 Hệ thống
● Monthly Closing ABC         🏢 Phòng Kế toán
```

ADMIN được attach resource Global nhưng không được chỉnh sửa resource Global.

---

## 7. Tool rule

Tool là ngoại lệ bảo mật quan trọng.

```text
SUPER_ADMIN
→ tạo/approve Tool implementation
→ quản lý Global Tool Registry
→ quản lý credential/security policy

ADMIN
→ thấy Tool global được phép
→ dùng/attach Tool vào Agent của phòng
→ không được sửa implementation
→ không được upload arbitrary executable code
→ không được chọn credential tùy ý

USER
→ không quản lý Tool
→ chỉ sử dụng gián tiếp qua Agent
```

---

## 8. USER experience

USER không có Workspace quản trị.

USER chỉ thấy:

```text
My Agents
Chat
Nghiệp vụ được phép
Business pages được Agent/capability cho phép
```

USER chỉ sử dụng Published Agent Version đã được ADMIN assign.

Có thể hiển thị read-only:

- tên Agent
- mô tả
- capability / nghiệp vụ hỗ trợ
- trạng thái Published

Không hiển thị:

- raw system prompt
- credentials / secrets
- internal tool configuration
- draft versions
- raw workflow JSON nội bộ

---

## 9. TENANT_OWNER

TENANT_OWNER = Giám đốc/Tenant Manager.

Quản lý:

```text
Tenant
├── Departments
├── Department ADMINs
├── Users
├── Move users giữa Department
├── Enable/Disable accounts
├── Tenant Usage
├── Tenant Audit
└── Reports
```

Không quản lý operational AI resources:

- không edit/publish Agent
- không edit Workflow
- không edit Skill
- không edit Knowledge
- không attach Tool

Có thể xem overview nếu cần.

---

## 10. SUPER_ADMIN

SUPER_ADMIN quản lý:

```text
Platform / System
├── Tenants
├── Tenant Owners (quản lý bên trong Tenant UI)
├── Models / Providers
├── Global Tools
├── Global Skills
├── Global Workflows/Templates
├── Global Knowledge/Templates
├── Global Agent Templates
├── Global Capability Catalog
├── Usage / Billing
├── Platform Audit
├── Health
└── Settings
```

`Tenant Owners` không cần menu riêng nếu Tenant Detail đã quản lý đúng một active Tenant Owner.

---

## 11. Nguyên tắc bảo mật cuối cùng

```text
GLOBAL RESOURCE
→ mọi ADMIN được READ + USE
→ chỉ SUPER_ADMIN được EDIT

DEPARTMENT RESOURCE
→ chỉ đúng tenant + đúng department được thấy
→ ADMIN cùng phòng được quản lý
→ USER chỉ consume qua Agent được assign
```

Backend là authorization authority cuối cùng.

Frontend chỉ quyết định cách hiển thị, không phải security boundary.

---

## 12. Câu chốt kiến trúc

> **Tài nguyên có sẵn của hệ thống thuộc scope Global: tất cả ADMIN đều nhìn thấy và sử dụng được nhưng không được chỉnh sửa. Tài nguyên do phòng ban tạo thuộc scope Department: chỉ phòng ban đó nhìn thấy và sử dụng. ADMIN quản lý tất cả trong một Workspace thống nhất; Global và Department chỉ được phân biệt bằng badge/quyền trong cùng danh sách, không tách thành menu riêng.**

---

## 13. Trạng thái triển khai

Trong schema hiện tại, tên lưu trữ tương ứng với “Global” là `platform`
(`agents.v2_scope` dùng `platform_template`). Đây chỉ là ánh xạ tên; invariant
vẫn là `tenant_id IS NULL AND department_id IS NULL`.

Các API ADMIN dưới `/department/**` trả danh sách hợp nhất bằng predicate:

```sql
scope = 'platform'
OR (scope = 'department'
    AND tenant_id = @principalTenantId
    AND department_id = @principalDepartmentId)
```

Mọi API mutation Department vẫn dùng riêng predicate `scope='department'`
cùng đúng tenant/department. Vì vậy UUID của Global, phòng khác hoặc tenant
khác luôn trả `404`/deny, không phụ thuộc việc UI có ẩn nút hay không.

Workspace ADMIN đã thống nhất tên và menu: `Agents`, `Năng lực`, `Workflows`,
`Skills`, `Knowledge`, `Tools`. Các danh sách dùng badge `Hệ thống` và
`Phòng ban`, không có tab/menu scope riêng. Tool vẫn chỉ có registry toàn cục;
ADMIN được xem và attach Tool active đã duyệt, không có endpoint tạo executable
Tool dưới `/department`.

Agent Builder lấy selector hợp nhất từ chính các endpoint principal-scoped.
Capability, Workflow, Skill và Knowledge attachment đều chấp nhận Global hoặc
own Department và fail-closed với scope khác. Business page tiếp tục gửi
`capability_code`; USER menu tiếp tục được suy ra từ capability của assigned,
published Agent, không từ catalog quản trị.

---

## 14. Business Module Department ACL (Kế toán / Chứng từ / Hợp đồng /
    Khách hàng / Nhà cung cấp / Chi phí)

Khác với §3 (Agent/Skill/Workflow/Tool là tài nguyên *governance*), các module
nghiệp vụ này thao tác trên **dữ liệu vận hành** (vouchers, contracts, sim
customer/supplier master data). Model áp dụng ở đây:

```text
Resource luôn thuộc Department (tenant_id + department_id, không phải chỉ
created_by). created_by chỉ giới hạn quyền EDIT của USER, không phải
visibility — mọi thành viên cùng Department đều READ được record của nhau.

USER (có Agent được assign + capability đúng module):
  - READ mọi record trong Department mình
  - CREATE/UPLOAD nếu module đó thực sự hỗ trợ intake (Vouchers, Contracts)
  - EDIT chỉ record do chính USER tạo (created_by), và chỉ khi còn ở trạng
    thái editable (chưa verified/journalized/rejected, hoặc chưa
    APPROVED/HANDED_OFF/REJECTED)
  - KHÔNG delete, KHÔNG approve/reject/apply (post AR/AP, decide journal,
    review/handoff hợp đồng) — mọi hành động "quyết định" là department_admin

ADMIN:
  - Toàn quyền trên mọi record trong Department mình, không phân biệt
    created_by
  - Riêng có: delete, apply-receipt/apply-invoice/apply-expense, decide
    journal suggestion, review/approve/reject/handoff hợp đồng
  - KHÔNG thấy Department khác, KHÔNG thấy Tenant khác

Khách hàng (customer.management) / Nhà cung cấp (supplier.management):
  read-only theo Department — hai module này không có endpoint tạo/sửa qua
  HTTP (dữ liệu đến từ vận hành voucher/ledger), nên "Add/Create" không áp
  dụng — đúng với nguyên tắc "chỉ thêm nút Add khi module thực sự hỗ trợ
  intake".
```

Triển khai: `Aiq.Gateway.Security.BusinessModuleAuthorization.TryScope` đọc
`tenant_id`/`department_id` từ principal đã xác thực (không bao giờ từ
browser payload) và `RequireBusinessCapability(...)` gate theo capability
code đúng module (`voucher.processing`, `customer.management`,
`supplier.management`, `expense.processing`, `contract.processing` /
`contract.intake`). Mỗi repository (`IVoucherRepository`,
`ICustomerRepository`, `ISupplierRepository`, `IContractRepository`,
`IChecklistRepository`) nhận `departmentId` thay vì `userId` để scope
READ/LIST; endpoint tự kiểm `existing.CreatedBy == principal.UserId ||
principal.IsInRole("admin")` trước khi cho EDIT — cùng một pattern lặp lại ở
cả 5 module, không có triển khai USER riêng song song với ADMIN.

Migration `030_business_module_department_acl.sql` backfill `department_id`
cho `vouchers`/`sim_customers`/`sim_suppliers` từ `created_by`/tenant mặc định
cho dữ liệu cũ. `contracts`/`contract_checklist_items` đã có `department_id`
từ trước (migration 019/029).

E2E: `scripts/test_business_module_acl_e2e.sh` (Vouchers/Customers/Suppliers)
và `scripts/test_contract_processing_e2e.sh` (Contracts) xác nhận đủ các
trường hợp USER-with-capability / USER-without-capability / edit-own /
cannot-edit-others / ADMIN-full-department / cross-department-denied /
cross-tenant-denied.
