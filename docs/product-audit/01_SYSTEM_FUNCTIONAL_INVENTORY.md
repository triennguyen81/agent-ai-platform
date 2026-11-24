# 01 — Kiểm kê chức năng hệ thống AIQ

Ngày: 2026-11-19 · Mã nguồn: nhánh `feat/phase-0-risk-tax` @ `b0d7aaf` + thay đổi web chưa commit (đăng nhập → chat).
Bằng chứng gốc: [evidence/gateway-routes.tsv](evidence/gateway-routes.tsv) (364 route Gateway + policy), [evidence/web-pages.tsv](evidence/web-pages.tsv) (83 trang web + điều kiện truy cập).

## Quy ước

**Vai trò** (thực tế trong mã, `gateway/src/Aiq.Gateway/Governance/PrincipalAuthorizationPolicy.cs`):
- **SA** = SUPER_ADMIN (nền tảng, không thuộc tenant/phòng ban) — policy `platform_admin`
- **TO** = TENANT_OWNER (quản trị tenant, không thuộc phòng ban) — policy `tenant_governance`
- **AD** = ADMIN phòng ban — policy `department_admin`
- **US** = USER phòng ban — cùng AD thuộc policy `department_member`

**Trạng thái** (cột "TT"):
- **PASS** — đủ chuỗi UI → API → service → DB và có test **chạy trong đợt audit này** (hoặc kiểm tra live) bao trùm hành vi chính.
- **PARTIAL** — có một phần; thiếu nghiệp vụ, UI, API hoặc tầng dữ liệu.
- **MISSING** — chưa có chức năng thực tế.
- **BROKEN** — có triển khai nhưng lỗi / không chạy.
- **UNVERIFIED** — có triển khai nhưng chưa đủ bằng chứng trong đợt này. Ký hiệu **UNVERIFIED·IT** = có integration test cần DB (chưa chạy theo nguyên tắc không ghi DB); **UNVERIFIED·0** = không có test tự động.

**Test chạy trong đợt này** (2026-11-19): Gateway non-DB 1.211/1.211 pass · Agent 411 pass / 8 skip · Web vitest 107/107. Gateway DB integration (494 test, `gateway/tests/Aiq.Gateway.Tests/Data/`) **không chạy**.

Cột "HD" = mục hướng dẫn sử dụng tương ứng (08 = người dùng, 09 = quản trị, 10 = kế toán; "—" = không viết hướng dẫn vì chưa hoạt động).

Kết quả kiểm tra live (đăng nhập bằng tài khoản demo, chỉ xem) được cập nhật ở [02_FUNCTIONAL_AUDIT_REPORT.md](02_FUNCTIONAL_AUDIT_REPORT.md) §Live.

---

## M01 — Xác thực (Authentication)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| AUTH-01 | Đăng nhập email/mật khẩu | Tất cả | `/login` | `POST /auth/login` (rate limit 10/phút) | `AuthEndpoints.cs`, `users`, JWT HS256 | `IdentityRulesTests`, `UserRepositoryPasswordIntegrationTests` (IT) | PASS | Kiểm tra live ở §Live; Live: đăng nhập thành công 4 vai trò (UI + API) | 08 §2 |
| AUTH-02 | Chuyển trang sau đăng nhập theo vai trò (AD/US → chat, TO/SA → dashboard) | Tất cả | `/login`, `/`, `/chat` | — | `web/src/lib/default-agent.ts` | `default-agent.test.ts` (5 test) | PASS | Thay đổi chưa commit | 08 §2 |
| AUTH-03 | Đăng nhập qua Clerk (SSO) | Tất cả | `/sign-in` | `POST /auth/external-link` (shared secret) | `api/auth/sync/route.ts` | — | UNVERIFIED·0 | Clerk chưa cấu hình trên môi trường demo (`CLERK_SECRET_KEY` rỗng trong `.env.example`) | — |
| AUTH-04 | Tự đăng ký tài khoản | Công khai | `/signup` | `POST /auth/register` | tạo USER trong tenant `default` | — | PARTIAL | **Rủi ro HIGH**: ai cũng tự tạo được USER trong tenant `default` (SEC-01) | — |
| AUTH-05 | Đổi mật khẩu / buộc đổi mật khẩu lần đầu | Tất cả | `/profile` | `POST /me/change-password` | `must_change_password` | `UserRepositoryPasswordIntegrationTests` (IT) | UNVERIFIED·IT | | 08 §2 |
| AUTH-06 | Đăng xuất | Tất cả | menu trái | — (xoá cookie) | `lib/auth.ts clearSession` | — | UNVERIFIED·0 | | 08 §2 |
| AUTH-07 | Thông tin phiên `/me` | Tất cả | mọi trang | `GET /me` | `Program.cs:601` | — | PASS | Live: dùng ở mọi trang đã duyệt | — |

## M02 — Quản trị nền tảng (SUPER_ADMIN)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| PLT-01 | Danh sách / tạo tenant | SA | `/admin/tenants` | `GET/POST /tenants` | `TenantEndpoints.cs`, `tenants` | `TenantQuotaPolicyTests` | PASS | Live: SA xem danh sách tenant (`super_admin-admin_tenants.png`); tạo tenant chưa chạy | 09 §2 |
| PLT-02 | Onboard tenant (tạo tenant + owner) | SA | `/admin/tenants` | `POST /system/tenants/onboard` | `TenantEndpoints.cs` | — | UNVERIFIED·0 | | 09 §2 |
| PLT-03 | Hạn mức (quota) tenant | SA | `/admin/tenants/[id]` | `GET/PATCH /tenants/{id}/quota` | | `TenantQuotaPolicyTests` | PASS | | 09 §2 |
| PLT-04 | Tổng quan tenant | SA | `/admin/tenant-status` | `GET /system/tenants/summary` |  | — | PASS | Live: số liệu tổng quan hiển thị trong `/admin/tenants` (SA) | 09 §2 |
| PLT-05 | Danh mục Agent nền tảng (tạo, version, publish, gán tenant, clone sang phòng ban, quarantine) | SA | `/admin/agent-builder` | `/system/agents/*` (17 route) | `DynamicAgentEndpoints.cs` | `DynamicAgentRepositoryIntegrationTests` (IT), `test_dynamic_runtime.py` | UNVERIFIED·IT | | 09 §5 |
| PLT-06 | Danh mục Skill nền tảng | SA | `/system/skills` | `/system/skills/*` | | — | UNVERIFIED·0 | | 09 §5 |
| PLT-07 | Danh mục Capability (nghiệp vụ) | SA | `/system/capabilities` | `/system/capabilities/*` | | `CapabilityResolutionIntegrationTests` (IT) | UNVERIFIED·IT | | 09 §5 |
| PLT-08 | Tri thức nền tảng | SA | `/system/knowledge` | `/system/knowledge/*` | | — | UNVERIFIED·0 | | 09 §5 |
| PLT-09 | Danh mục Tool | SA (xem: SA/AD) | `/admin/tools` | `/tools/*` | `tools` | `ResourceOrToolPolicyTests` | UNVERIFIED·0 | Tool thuế chỉ được đăng ký bằng script demo, không qua migration | 09 §5 |
| PLT-10 | Danh mục Workflow mẫu | SA | `/system/workflows` → `/admin/workflows` | `/workflows/*` | | — | UNVERIFIED·0 | | — |
| PLT-11 | Cấu hình Model/Provider | SA | `/system/models` | — | — | — | MISSING | Trang tĩnh: "cấu hình qua biến môi trường" | — |
| PLT-12 | Cài đặt nền tảng | SA | `/system/settings` | — | — | — | MISSING | Trang tĩnh, không có chức năng | — |
| PLT-13 | Quản lý Tenant Owner | SA | `/system/tenant-owners` → `/admin/tenants` | — | | — | PARTIAL | Chỉ là chuyển hướng | 09 §2 |

## M03 — Tenant

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| TEN-01 | Hồ sơ công ty (tên pháp lý, MST, địa chỉ, bí danh) | TO | `/tenant/company-profile`, `/admin/company-profile` | `/tenant/company-profile` | `tenant_company_profiles` | `TenantCompanyProfileIntegrationTests` (IT) | UNVERIFIED·IT | MST công ty là đầu vào bắt buộc của validator hoá đơn | 09 §3 |
| TEN-02 | Hồ sơ kinh doanh | TO | `/tenant/business-profile` | `/tenant/business-profile` | | — | UNVERIFIED·0 | | 09 §3 |
| TEN-03 | Xem hạn mức tenant | AD/TO | `/admin/tenant-status` | `GET /tenant/quota` |  | `TenantQuotaPolicyTests` | PASS | UI thực tế: `/admin/tenant-status` (chỉ TO) gọi `/tenant/quota` | — |
| TEN-04 | Quản lý ADMIN phòng ban | TO | `/tenant/department-admins` → `/admin/tenant-users?role=admin` | `/tenant/admins/*` | | `TenantUserRoleChangePolicyTests` | PASS | | 09 §3 |
| TEN-05 | Thành viên nhiều tenant | Tất cả | — | `GET /me/tenants` | | — | PARTIAL | Không có UI chuyển tenant | — |

## M04 — Tổ chức & phòng ban

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| ORG-01 | Tạo / sửa / khoá phòng ban | TO | `/admin/tenant-departments` | `/tenant/departments/*` | `departments` | `DepartmentManagementIntegrationTests` (IT), `DepartmentAccessPolicyTests` | UNVERIFIED·IT | | 09 §3 |
| ORG-02 | Thành viên phòng ban | AD | `/department/members` | `/tenant/users` (scope phòng ban) |  | `DepartmentRbacIntegrationTests` (IT) | PASS | Live: AD xem thành viên (`/department/members` → `/admin/tenant-users`) | 09 §4 |
| ORG-03 | Phòng ban mặc định khi tạo tenant | Hệ thống | — | | migration 019 | `DepartmentMigrationIntegrationTests` (IT) | UNVERIFIED·IT | | — |

## M05 — Người dùng

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| USR-01 | Tạo / sửa / khoá / mở khoá người dùng | TO (toàn tenant), AD (phòng mình) | `/admin/tenant-users`, `/tenant/users` | `/tenant/users/*` | `users`, `user_roles` | `TenantUserRoleChangePolicyTests`, `IdentityRulesTests` | PASS | | 09 §4 |
| USR-02 | Đặt lại mật khẩu người dùng | TO/AD | `/admin/tenant-users` | `POST /tenant/users/{id}/reset-password` | | — | UNVERIFIED·0 | | 09 §4 |
| USR-03 | Đổi vai trò người dùng | TO | `/admin/tenant-users` | `PATCH /tenant/users/{id}` | | `TenantUserRoleChangePolicyTests` | PASS | | 09 §4 |
| USR-04 | Hồ sơ cá nhân | Tất cả | `/profile` | `/me` |  | — | PASS | Live: 2 vai trò mở `/profile` | 08 §2 |

## M06 — Vai trò & phân quyền

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| RBAC-01 | 4 vai trò cố định (SA/TO/AD/US) + policy | — | — | toàn bộ Gateway | `PrincipalAuthorizationPolicy.cs`, `Program.cs:366-400` | `PrincipalAuthorizationPolicyTests`, `DepartmentAccessPolicyTests`, `ExecutionAuthorizationTests`, `AdminDepartmentAuthorityInvariantTests` | PASS | Không có role chi tiết (kế toán trưởng, chỉ xem…) — theo thiết kế | 09 §4 |
| RBAC-02 | Policy legacy `admin`/`user`/`uploader` (theo role, không theo phòng ban) | — | — | 34 route (agents legacy, approval-queue, tenant users, notifications, uploads) | `Program.cs:384-390` | — | PARTIAL | `/approval-queue` cho TO duyệt chứng từ (ACC-01) | — |
| RBAC-03 | Agent ACL (gán người dùng cho Agent) | AD | `/department/agent-assignments` | `/department/agents/{id}/users/*` | `agent_assignments` | `AgentPrincipalVisibilityIntegrationTests` (IT) | UNVERIFIED·IT | | 09 §5 |

## M07 — Agent Builder

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| AGB-01 | Tạo Agent phòng ban | AD | `/department/agents` | `POST /department/agents` | `agents` | `DepartmentAgentOwnershipIntegrationTests` (IT) | UNVERIFIED·IT | | 09 §5 |
| AGB-02 | Version Agent: soạn, validate, publish | AD | `/department/agents/[id]` | `/department/agents/{id}/versions/*` | `agent_versions` | `test_dynamic_runtime.py` | UNVERIFIED·IT | | 09 §5 |
| AGB-03 | Gắn Skill / Tool / KB / Capability vào version | AD | `/department/agents/[id]` | versions PATCH, `/department/capabilities/*/binding`, `/department/knowledge-bases/{id}/agents/*` | | `ManifestAccessRulesTests` | PASS | | 09 §5 |
| AGB-04 | Cấu hình xử lý chứng từ của Agent (loại chứng từ, trường) | AD | `/department/agents/[id]` | `.../versions/{v}/document-processing`, `.../document-processing/test` | `agent_version_document_types` | `DocumentProcessingUnitTests`, `test_document_processing.py` | PASS | | 09 §5 |
| AGB-05 | Bật / tắt Agent | AD, SA | `/department/agents/[id]` | `.../enable`, `.../disable` | | — | UNVERIFIED·0 | | 09 §5 |
| AGB-06 | Agent Builder legacy (`/agents`, policy `admin`) | AD/TO/SA | `/admin/agents` → chuyển hướng | `/agents/*` (24 route) | | — | PARTIAL | Song song 2 bộ API Agent (legacy + department) | — |
| AGB-07 | Skill phòng ban | AD | `/department/skills` | `/department/skills/*` | | `DepartmentWorkflowSkillKnowledgeIntegrationTests` (IT) | UNVERIFIED·IT | | 09 §5 |
| AGB-08 | Workflow phòng ban (chỉ dẫn từng bước chèn vào prompt) | AD | `/department/workflows` | `/department/workflows/*` | | (IT như trên) | PARTIAL | Không phải công cụ chạy quy trình; chỉ là chỉ dẫn cho LLM | 09 §5 |
| AGB-09 | Capability phòng ban (gắn nghiệp vụ ↔ Agent) | AD | `/department/capabilities` | `/department/capabilities/*` | | `capability.test.ts`, `CapabilityResolutionIntegrationTests` (IT) | PASS | | 09 §5 |

## M08 — Gán Agent cho người dùng

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test hiện có | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| ASG-01 | Gán / thu hồi Agent cho USER | AD | `/department/agent-assignments` | `PUT/DELETE /department/agents/{id}/users/{userId}` | `agent_assignments` | `AgentDepartmentEntitlementIntegrationTests` (IT) | UNVERIFIED·IT | | 09 §5 |
| ASG-02 | Danh sách Agent của tôi | AD/US | `/my-agents` | `GET /me/agents` |  | — | PASS | Live: AD, US mở `/my-agents` | 08 §3 |
| ASG-03 | Chọn Agent mặc định khi vào chat | AD/US | `/chat` | `GET /me/agents` | `pickDefaultAgent` | `default-agent.test.ts` | PASS | | 08 §3 |

## M09 — Agent Runtime

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Agent/tool | Test hiện có | TT | Thiếu sót / rủi ro |
|---|---|---|---|---|---|---|---|---|---|
| RUN-01 | Manifest ký bởi Gateway là nguồn quyền duy nhất (tool, skill, KB, model) | Hệ thống | — | `GET /runtime/agents/{id}/manifest` | `runtime/manifest.py`, `executors.py` | — | `test_runtime_security.py`, `test_dynamic_runtime.py`, `ManifestAccessRulesTests` | PASS | |
| RUN-02 | Allowlist tool fail-closed khi tra cứu quyền lỗi | Hệ thống | — | `/runtime/agents/{id}/authorization` | `agentic/runner.py:140-157` | — | `test_registry.py` | PASS | |
| RUN-03 | Giới hạn số vòng tool (cap 12) | Hệ thống | — | | `config.py agent_max_turns_cap` | — | `test_runtime_configuration.py` | PASS | |
| RUN-04 | Ghi usage / chi phí token theo agent | Hệ thống | — | `POST /runtime/usage` | `agent_usage` | — | `test_cost_tracking.py`, `test_token_usage.py`, `AgentUsageTokenDetailIntegrationTests` (IT) | PASS | |
| RUN-05 | Uỷ quyền router → specialist agent | Hệ thống | — | `/runtime/agents/{router}/delegate/{specialist}/manifest` | | — | `test_dynamic_runtime.py` | PASS | |
| RUN-06 | Chat không gắn agent (legacy `/agent/run`) | — | — | Agent `/agent/run` | `runner.py` | — | `test_runtime_configuration.py` | PASS | Tắt mặc định (`legacy_agent_run_enabled=False`) |

## M10 — Planner – Executor – Critic

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro |
|---|---|---|---|---|---|---|---|---|
| PEC-01 | Planner–Executor–Critic | SA/TO/AD khi chat không chọn agent | — | Agent `POST /chat` (nhánh không manifest) | `orchestration/pec.py`, `api/routes.py:285` | — | PARTIAL | Chỉ dùng ở nhánh chat **không gắn agent**, tối đa 1 tool; luồng chat thật của AD/US đi qua manifest executor, **không có bước Critic** đối chiếu câu trả lời với output tool |

## M11 — Chat & hội thoại

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| CHAT-01 | Chat với Agent đã được cấp | AD/US | `/chat?agent=` | web `/api/chat` → Agent `POST /chat` | `executors.py`, Redis memory | `test_memory.py`, `test_runtime_security.py` | PASS | Live: 15 lượt chat chỉ đọc (AD 7, US 8) — [05](05_AI_AGENT_AUDIT.md) | 08 §4 |
| CHAT-02 | Lịch sử hội thoại theo agent (thread) | AD/US | sidebar | `/api/chat/threads` | Redis | `chat-history.test.ts`, `test_memory.py` | PASS | | 08 §4 |
| CHAT-03 | Đổi agent đang dùng | AD/US | sidebar "Trợ lý của bạn" | `POST /api/me/active-agent` | cookie `aiq.active_agent` | — | UNVERIFIED·0 | | 08 §4 |
| CHAT-04 | Cửa sổ lịch sử gửi cho model (20 tin / 24k ký tự) | Hệ thống | — | | `config.py history_max_*` | `test_token_optimization.py` | PASS | | — |
| CHAT-05 | Lưu câu trả lời vào KB ("Save to KB") | AD/US | nút trên kết quả | `/notes` | `notes` | — | UNVERIFIED·0 | | 08 §5 |
| CHAT-06 | Câu hỏi gợi ý trên màn hình chat | AD/US | `/chat` | — | `components/chat-room.tsx` (hardcode) | — | PARTIAL | Live: 4 câu gợi ý là **nội dung nha khoa** ("Implant Straumann…", "tiệt trùng dụng cụ nha khoa") cho agent kế toán — sót từ demo cũ | 08 §4 |

## M12 — Knowledge Base

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| KB-01 | Knowledge base phòng ban: tạo, tải tài liệu, xử lý, gắn agent | AD | `/department/knowledge` | `/department/knowledge-bases/*` | `knowledge_bases`, `document_versions`, `document_chunks` | `KnowledgeBaseIntegrationTests` (IT), `test_knowledge.py`, `knowledge-document-form.test.ts`, `route.test.ts` | UNVERIFIED·IT | | 08 §5 |
| KB-02 | Ghi chú tri thức phòng ban (notes) + archive/restore | AD | `/department/knowledge` | `/department/knowledge/*` | `notes` | (IT) | UNVERIFIED·IT | | 08 §5 |
| KB-03 | Ghi chú cá nhân | Tất cả | — | `/notes/*` | `notes` | — | PARTIAL | Không có trang riêng (`/knowledge` chuyển hướng sang trang phòng ban) | — |
| KB-04 | Gắn tri thức vào agent | AD | `/department/agents/[id]` | `/department/agents/{id}/knowledge/*` | `agent_knowledge` | (IT) | UNVERIFIED·IT | | 09 §5 |

## M13 — RAG & Vector Search

| ID | Chức năng | Vai trò | Backend | Test | TT | Thiếu sót / rủi ro |
|---|---|---|---|---|---|---|
| RAG-01 | Truy xuất theo phạm vi KB của manifest (agent chỉ đọc KB được cấp) | Hệ thống | `executors.py _scope_knowledge_tool`, `knowledge/service.py`, pgvector `document_chunks` | `test_knowledge.py`; `test_knowledge_integration.py` (8 test **skip**: cần `TEST_KNOWLEDGE_DSN`) | UNVERIFIED·IT | Kiểm tra live §Live |
| RAG-02 | Embedding + truy vấn (`/embeddings/query`) | Hệ thống | `embeddings/service.py` (Qdrant + pgvector) | `test_knowledge.py` | PARTIAL | Hai vector store song song (Qdrant cho embeddings cũ, pgvector cho KB) |
| RAG-03 | Trích dẫn nguồn (skill `rag-citation`) | Hệ thống | skill prompt | — | UNVERIFIED·0 | Phụ thuộc LLM tuân thủ |

## M14 — OCR & trích xuất chứng từ

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| OCR-01 | OCR PDF (ưu tiên text layer, sau đó OCR ảnh) | Hệ thống | — | Agent `/ocr/pdf` | `agent/ocr/*` | `test_ocr_pipeline.py`, `test_ocr_text_layer.py`, `test_ocr_concurrency.py` | PASS | Native OCR có tiền sử crash khi chạy song song (đã serialise ở Gateway) | 10 §1 |
| OCR-02 | Trích xuất trường chứng từ bằng Agent (LLM) theo cấu hình loại chứng từ | Hệ thống | — | Agent `/agent/document-extract` | `documents/extraction.py`, `Jobs/VoucherExtractJobRunner.cs` | `test_document_extraction.py`, `DocumentProcessingIntegrationTests` (IT) | UNVERIFIED·IT | Phụ thuộc hạn mức subscription của model | 10 §1 |
| OCR-03 | Phân loại loại chứng từ | Hệ thống | — | | `VoucherTypeClassifier.cs` | `VoucherTypeClassifierTests`, `voucher-type.test.ts` | PASS | | 10 §1 |
| OCR-04 | Đọc XML hoá đơn điện tử | — | — | — | — | — | MISSING | `extraction.py:15` ghi rõ chưa có parser XML HĐĐT | — |
| OCR-05 | Đọc sao kê ngân hàng (CSV/XLSX) | Hệ thống | `/vouchers/upload` | `/vouchers/upload` | `Banking/BankStatementParser.cs` | `BankStatementParserTests`, `bank-statement.test.ts` | PASS | | 10 §4 |
| OCR-06 | Trích xuất hợp đồng | AD/US | `/contracts/upload` | `/contracts/extract-text`, `/contracts/{id}/process` | `contract_tools.py` | `test_contract_normalize.py`, `test_contract_tools_normalize.py`, `route.test.ts` | PASS | | 10 §6 |
| OCR-07 | Idempotency job OCR (không xử lý trùng) | Hệ thống | — | | Hangfire | `VoucherOcrJobIdempotencyIntegrationTests` (IT) | UNVERIFIED·IT | | — |

## M15 — Quản lý tài liệu

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| DOC-01 | Lưu file gốc theo tenant (MinIO/S3) | Hệ thống | — | | `Storage/*` | `TenantStorageTests`, `DocumentExtractionTenantIsolationTests`, `test_tenant_storage.py` | PASS | | — |
| DOC-02 | Xem trước chứng từ | AD/US | `/vouchers/[id]` | `GET /vouchers/{id}/preview` | | — | UNVERIFIED·0 | | 10 §1 |
| DOC-03 | Xuất chứng từ (zip) | AD/US | `/vouchers` | `GET /vouchers/export` | `VoucherExportService.cs` | — | UNVERIFIED·0 | | 10 §1 |
| DOC-04 | Sách PDF (Books) — tải trang, OCR | uploader | **không có UI** | `/books/*` | `Books/*` | `AgentClientTests` | PARTIAL | Chỉ có API | — |
| DOC-05 | Mẫu chứng từ (thư viện ví dụ) | Tất cả | `/mau-chung-tu` | — | `data/artifacts-manifest.ts` (tĩnh) | — | PASS | Nội dung tĩnh | 10 §1 |

## M16 — Kế toán chung (bút toán, kỳ kế toán)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Agent/tool | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|---|
| ACC-01 | Đề xuất bút toán (Agent) — chỉ lưu đề xuất | AD/US qua Agent | `/vouchers/[id]` | `POST /vouchers/{id}/journal-suggestions` | `voucher_journal_suggestions`, `JournalRoutingRules.IsAllowed` | `suggest_journal_entry`, `save_journal_suggestions` | `JournalRoutingRulesTests`, `test_save_voucher_conflicts.py` | PASS | Danh mục cặp TK cho phép chỉ áp cho receipt/expense_claim | 10 §2 |
| ACC-02 | Duyệt / từ chối đề xuất bút toán | AD | `/vouchers/[id]` | `POST .../journal-suggestions/{entryId}/decision` | | — | — | UNVERIFIED·0 | | 10 §2 |
| ACC-03 | Ghi sổ (posting) bất biến, đảo bút toán | AD | `/vouchers/[id]` | `.../settlement/post`, `.../settlement/reverse` | `cash_transactions`, trigger bất biến | — | `CashSettlementRulesTests`, `VoucherPostingGuardIntegrationTests` (IT) | PASS | | 10 §2 |
| ACC-04 | Kỳ kế toán: đóng / mở lại, khoá ghi sổ theo kỳ | AD | — (chưa có trang) | `/accounting-periods/*` | `accounting_periods` | — | `AccountingPeriodTests`, `AccountingPeriodEndpointTests` (IT) | PARTIAL | **Không có UI** quản lý kỳ; chỉ hoạt động ở tenant bật v2 — hiện không tenant nào bật | — |
| ACC-05 | Validator chứng từ (MST, cộng tiền, thuế suất theo ngày, ngày tương lai, kỳ khoá) | Hệ thống | `/vouchers/[id]` | `GET /vouchers/{id}/validation` | `VoucherValidator.cs`, `vat_rates` | — | `VoucherValidatorTests` (44) | PASS | Tenant không bật v2 chỉ cảnh báo | 10 §1 |
| ACC-06 | Vòng đời chứng từ v2 (submit → approve/return/reject) | AD duyệt, US nộp | `/vouchers/[id]` | `/vouchers/{id}/submit|approve|return|reject` | `VoucherStateMachine.cs` | — | `VoucherStateMachineTests`, `VoucherLifecycleIntegrationTests` (IT) | PASS | **Đang tắt trên mọi tenant demo**: cờ `voucher_flow_v2` chưa bật (`tenant_feature_flags` rỗng) → route trả `voucher_flow_v2_disabled` | 10 §1 |
| ACC-07 | Định khoản nhanh (công cụ) | AD/US | `/accounting/journal` | Agent capability | LLM | `journal_entry_validator` | — | PARTIAL | Kết quả là văn bản LLM | 10 §2 |
| ACC-08 | Sổ cái / bảng cân đối phát sinh | — | — | — | — | — | — | MISSING | Không có sổ cái tổng hợp | — |
| ACC-09 | Báo cáo tài chính (B01, B02, LCTT) | AD/US | `/accounting/reports` | Agent capability `accounting.report` | LLM | — | — | PARTIAL | LLM soạn khung từ KB/dữ liệu dán tay, **không lấy từ sổ**; tên công ty mặc định hardcode | 10 §7 |
| ACC-10 | Tra cứu tài khoản TT200 | AD/US | qua chat | | `lookup_account` | — | — | UNVERIFIED·0 | 60 TK phổ biến | 08 §4 |
| ACC-11 | Đọc hoá đơn từ văn bản dán | AD/US | `/accounting/invoice` | `POST /accounting/invoice/extract-text` | | | `route.test.ts` | PASS | | 10 §1 |
| ACC-12 | Hàng đợi chứng từ cần xử lý / báo cáo trạng thái | AD/US/TO | `/admin/voucher-status` | `GET /vouchers/status-report` |  | — | — | PASS | Live: TO, AD mở `/admin/voucher-status` | 10 §7 |

## M17 — Quản lý chứng từ (Voucher)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| VOU-01 | Tải chứng từ (ảnh/PDF/CSV/XLSX ≤25 MB) | AD/US | `/vouchers/upload` | `POST /vouchers/upload` | `vouchers`, MinIO | `DocumentGateUnitTests` | PASS | Kiểm tra theo content-type/đuôi file, không kiểm tra magic bytes | 10 §1 |
| VOU-02 | Danh sách / lọc / chi tiết chứng từ | AD/US | `/vouchers`, `/vouchers/[id]` | `GET /vouchers`, `GET /vouchers/{id}` | | `voucher-view.test.ts` | PASS | | 10 §1 |
| VOU-03 | Sửa trường trích xuất | AD/US | `/vouchers/[id]` | `PATCH /vouchers/{id}` | | — | UNVERIFIED·0 | Không sửa được chứng từ đã ghi sổ (guard) | 10 §1 |
| VOU-04 | Trích xuất lại | AD/US | `/vouchers/[id]` | `POST /vouchers/{id}/reextract` | | — | UNVERIFIED·0 | | 10 §1 |
| VOU-05 | Phát hiện hoá đơn trùng | Hệ thống | — | | `FindDuplicateAsync` | — | PARTIAL | Khoá chỉ `seller_mst + số HĐ`, không ký hiệu/năm, tự **từ chối** chứng từ (TAX-H02) | 10 §1 |
| VOU-06 | Xác thực hoá đơn với cơ quan thuế (ADMIN chứng thực có bằng chứng; Agent chỉ đề xuất) | AD xác nhận; Agent/US đề xuất | `/vouchers/[id]` | `/vouchers/{id}/tax-verification/*` | `TaxVerification.cs`, `voucher_tax_verification_events` | `TaxVerificationEndpointPolicyTests`, `tax-verification.test.ts`, `test_tax_verification_tools.py`, `TaxVerificationIntegrationTests` (IT) | PASS | Không có tích hợp tự động với cổng TCT (ADMIN tra tay) | 10 §1 |
| VOU-07 | Tra cứu HĐĐT tự động (`verify_einvoice_tct`) | Agent | — | hoadondientu.gdt.gov.vn | `voucher_tools.py:41` | — | PARTIAL | Best-effort, rơi về kiểm tra định dạng MST; kết quả chỉ là đề xuất | — |
| VOU-08 | Xoá chứng từ | AD | `/vouchers/[id]` | `DELETE /vouchers/{id}` | | — | UNVERIFIED·0 | | 10 §1 |
| VOU-09 | Gán loại chứng từ thủ công | AD | `/vouchers/[id]` | `POST /vouchers/{id}/voucher-type` | | — | UNVERIFIED·0 | | 10 §1 |
| VOU-10 | Duyệt tài liệu theo task (document review) | AD | `/vouchers/[id]` | `/vouchers/{id}/document-review/*` | `document_review_tasks` | `DocumentGateHardeningIntegrationTests` (IT) | UNVERIFIED·IT | | 10 §1 |
| VOU-11 | Liên kết chứng từ ↔ hợp đồng | AD/US | `/vouchers/[id]` | `PATCH /vouchers/{id}/link-contract` | | — | UNVERIFIED·0 | | 10 §6 |

## M18 — Công nợ phải trả (AP)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| AP-01 | Danh mục nhà cung cấp (+ tài khoản ngân hàng) | AD (sửa), US (xem) | `/suppliers`, `/suppliers/[id]` | `/suppliers/*`, bank-accounts | `sim_suppliers` | `BankingEndpointPolicyTests`, `CounterpartyMatcherTests` | PASS | | 10 §3 |
| AP-02 | Ghi nhận hoá đơn mua (apply-expense) | AD | `/vouchers/[id]` | `POST /vouchers/{id}/apply-expense` | `sim_ap_ledger` | `ArApOpenItemIntegrationTests` (IT) | UNVERIFIED·IT | | 10 §3 |
| AP-03 | Công nợ & tuổi nợ NCC, tổng hợp AP | AD/US | `/suppliers/[id]` | `/suppliers/{id}/payables`, `/suppliers/ap-summary` |  | `get_supplier_payables`, `get_ap_summary` | — | PASS | Live: AD xem chi tiết NCC + công nợ (`admin-suppliers_id.png`); tool `get_supplier_payables` trả số liệu ở chat C01–C02 | 10 §3 |
| AP-04 | Thanh toán NCC từ sao kê (money_out → AP) | AD | `/vouchers/[id]` | bank-transactions settlement | | `statement-settlement.test.ts`, `CashSettlementIntegrationTests.StatementSettlement` (IT) | PARTIAL | Roadmap ghi "NEXT: money_out → AP" | 10 §4 |
| AP-05 | Cờ rủi ro AP (NCC mới, quá hạn, chi tiền mặt lớn, >500tr cần L2) | Hệ thống | `/admin/approval-queue` | | `VoucherExtractJobRunner.cs:1555-1610` | — | PARTIAL | Ngưỡng tiền mặt 30tr sai luật (TAX-C03) | 10 §5 |
| AP-06 | Đối chiếu đơn mua hàng (PO) | AD/US | — | `GET /purchase-orders/by-invoice/{no}` | bảng mô phỏng | — | PARTIAL | Dữ liệu mô phỏng (`sim_*`) | — |

## M19 — Công nợ phải thu (AR)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| AR-01 | Danh mục khách hàng (+ hạn mức tín dụng, tài khoản NH) | AD (sửa), US (xem) | `/customers`, `/customers/[id]` | `/customers/*` | `sim_customers` | `BankingEndpointPolicyTests` | PASS | | 10 §3 |
| AR-02 | Ghi nhận hoá đơn bán (apply-invoice) | AD | `/vouchers/[id]` | `POST /vouchers/{id}/apply-invoice` | `sim_ar_ledger` | `InvoiceDirectionResolverTests`, `ArApOpenItemIntegrationTests` (IT) | PASS | | 10 §3 |
| AR-03 | Khớp phiếu thu với công nợ (receipt match: đủ, thiếu, thừa, trả trước, ngoại tệ) | US đề xuất, AD duyệt | `/vouchers/[id]` | `/vouchers/{id}/receipt-match/*`, `apply-receipt` | `receipt_match_results` | `PayinCalculatorTests`, `JournalRoutingRulesTests` | PASS | Ngoại tệ bị chặn (chưa có nguồn tỷ giá) | 10 §3 |
| AR-04 | Thu tiền từ sao kê (money_in → AR) | AD | `/vouchers/[id]` | bank-transactions settlement | | `CashSettlementRulesTests`, `CounterpartyMatcherTests` | PASS | | 10 §4 |
| AR-05 | Chiết khấu thanh toán | AD | `/contracts/[id]`, `/vouchers/[id]` | `.../settlement/discount/confirm`, `/contracts/{id}/payment-discount` | | `PaymentDiscountCalculatorTests` | PASS | | 10 §3 |
| AR-06 | Nhắc nợ quá hạn (job hằng ngày → thông báo) | Hệ thống | `/notifications` | Hangfire | `AccountsReceivableOverdueJob.cs` | `AccountsReceivableOverdueJobTests` | PASS | Chỉ thông báo trong ứng dụng | 08 §6 |
| AR-07 | Đối chiếu đơn bán (SO) | AD/US | — | `GET /sales-orders/by-invoice/{no}` | bảng mô phỏng | — | PARTIAL | Dữ liệu mô phỏng | — |

## M20 — Chi phí & hoàn ứng

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| EXP-01 | Tạo đề nghị thanh toán / hoàn ứng nhiều file | AD/US | `/expenses/claims/new` | `POST /expense-claims`, `/items` | `expense_claims` | `ExpenseClaimItemRulesTests`, `ExpenseClaimMultiFileIntegrationTests` (IT) | PASS | | 10 §5 |
| EXP-02 | Kiểm tra theo chính sách chi (hạn mức, trùng, thiếu chứng từ) | Hệ thống | | `.../expense-claim-result/propose` | `department_expense_policies` | `ExpenseClaimItemRulesTests` | PASS | | 10 §5 |
| EXP-03 | Duyệt phòng ban → duyệt kế toán (ghi nhận chi phí Nợ 64x/1331, Có 141/334) | AD | `/vouchers/[id]` | `.../expense-claim/department-review`, `.../accounting-review` | `ExpenseClaimRecognition.cs` | `JournalRoutingRulesTests`, `ExpenseClaimRecognitionIntegrationTests` (IT), `ExpenseClaimSameAdminLifecycleTests` (IT) | PASS | VAT được khấu trừ chỉ dựa vào "có số HĐ" (TAX-C03) | 10 §5 |
| EXP-04 | Chi tiền hoàn ứng (Nợ 334 / Có 111x/112x) | AD | `/vouchers/[id]` | `POST .../expense-claim/pay` | `cash_transactions` | `expense-claim-payment.test.ts`, `test_expense_payment_tools.py`, `ExpenseClaimPaymentIntegrationTests` (IT) | PASS | Chỉ hỗ trợ số phải trả > 0; hoàn trả từ NV chưa có | 10 §5 |
| EXP-05 | Ngoại lệ chính sách | AD | `/vouchers/[id]` | `.../grant-expense-exception` | | — | UNVERIFIED·0 | | 10 §5 |
| EXP-06 | Danh sách chi phí / nhân viên | AD/US | `/expenses` | `/expense-claims/employees` | `sim_employees` | — | PASS | Live: AD, US mở `/expenses` | 10 §5 |

## M21–M23 — Thuế (chi tiết: [docs/audit/TAX_WORKFLOW_AUDIT_REPORT.md](../audit/TAX_WORKFLOW_AUDIT_REPORT.md))

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| TAX-01 | Bảng thuế suất GTGT theo ngày hiệu lực | Hệ thống | — | | `vat_rates` (migration 068) | `VoucherValidatorTests` | PARTIAL | Không có UI quản lý; thiếu các đợt giảm 8% trước 07/2025 | — |
| TAX-02 | Cấu hình quy tắc thuế có version, duyệt quy tắc | — | — | — | — | — | MISSING | Đã bỏ cùng Phase 0 | — |
| TAX-03 | Máy tính VAT (tách giá – thuế 1 hoá đơn) | AD/US | `/accounting/tax-calc` | capability `accounting.tax` | `calculate_vat` | probe 5/6 | PARTIAL | Kết quả hiển thị là văn bản LLM; float | 10 §8 |
| TAX-04 | Máy tính TNCN | AD/US | `/accounting/tax-calc` | như trên | `calculate_pit` | probe 1/7 | **BROKEN** | Dùng giảm trừ 11tr/4,4tr + biểu 7 bậc cho kỳ 2026 (TAX-C01). **Live C04**: Agent trả 1.627.500 ₫ cho gross 30tr (đúng kỳ 2026: 635.000 ₫) | — |
| TAX-05 | Máy tính TNDN | AD/US | `/accounting/tax-calc` | như trên | `calculate_cit` | probe 2/5 | **BROKEN** | Mặc định 20%, không xét 15%/17% (TAX-C02) | — |
| TAX-06 | Hạn nộp tờ khai | AD/US | `/accounting/deadline` + chat | — | `check_filing_deadline` + logic trùng trong trang web | probe 4/8 | **BROKEN** | Hạn quý ngày 30 (sai 3/4 quý), 02/QTT-TNCN sai, không dời ngày nghỉ (TAX-H01). **Live C07**: Agent trả 30/10/2026 cho 01/GTGT Q3 (đúng 31/10) | — |
| TAX-07 | Kê khai 01/GTGT, 05/QTT, 03/TNDN, tổng hợp thuế theo kỳ | — | — | — | — | — | MISSING | | — |
| TAX-08 | Dữ liệu lương cho TNCN | — | — | — | — | — | MISSING | | — |

## M24 — Quy trình phê duyệt

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| APR-01 | Hàng đợi duyệt rủi ro cao (L2) — legacy | AD (thực tế cả TO) | `/admin/approval-queue` | `/approval-queue/*` (policy `admin`) | `sim_approval_queue` | — | PARTIAL | **Không giới hạn phòng ban; TO duyệt được** (ACC-01). Duyệt chỉ đổi trạng thái | 09 §6 |
| APR-02 | Duyệt chứng từ v2 (approve/return/reject có lý do) | AD | `/vouchers/[id]` | `/vouchers/{id}/approve|return|reject` | | `VoucherStateMachineTests` | PASS | Cần bật cờ `voucher_flow_v2` cho tenant (hiện tắt) | 09 §6 |
| APR-03 | Hội đồng duyệt (review board, bỏ phiếu) | AD | `/admin/review-queue`, `/department/review` | `/review-boards/*`, `/review-requests/*`, `/me/review-queue` | `IReviewBoardRepository` | — | UNVERIFIED·0 | | 09 §6 |
| APR-04 | Nhiều cấp duyệt cấu hình được | — | — | — | — | — | MISSING | Roadmap mục 7 | — |

## M25 — Human-in-the-loop

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| HITL-01 | Cổng HITL theo agent (trigger rule) | AD | `/department/agents/[id]` | `/agents/{id}/gates`, `/gates/*` | `TriggerRuleEvaluator` | `TriggerRuleEvaluatorTests` | PASS | | 09 §6 |
| HITL-02 | Hàng đợi quyết định HITL | AD | `/admin/hitl-queue`, `/department/review` | `/hitl/queue`, `/hitl/{id}/decide` | | — | UNVERIFIED·0 | | 09 §6 |
| HITL-03 | Agent đánh giá HITL trước hành động | Agent | — | `/runtime/agents/{id}/hitl/evaluate` | | `ResourceOrToolPolicyTests` | PASS | | — |
| HITL-04 | Agent không tự duyệt / chi tiền / ghi sổ | Hệ thống | — | (không có tool) | `voucher_tools.py` chỉ gọi tạo, sửa, đề xuất | `test_expense_payment_tools.py`, `test_tax_verification_tools.py` | PASS | Khi ADMIN chat, token Agent mang quyền ADMIN; an toàn hiện tại dựa vào việc **không có tool**, chưa có chặn phía Gateway. Live: Agent từ chối ghi sổ/chi tiền khi bị prompt injection (C05) và khi USER yêu cầu duyệt (C15); không phát sinh ghi chép | — |

## M26 — Thông báo

| ID | Chức năng | Vai trò | UI | API | Backend | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| NOT-01 | Thông báo trong ứng dụng (danh sách, đã đọc, đếm chưa đọc) | Tất cả | `/notifications` | `/notifications/*` | `notifications` | — | PASS | Live: 3 vai trò xem danh sách; chưa thử đánh dấu đã đọc | 08 §6 |
| NOT-02 | Nguồn thông báo: nợ quá hạn, hạn hợp đồng, phạt trả chậm, khớp phiếu thu, đề nghị thanh toán | Hệ thống | | | Jobs + repository | `AccountsReceivableOverdueJobTests`, `ContractInstallmentPenaltyJobTests` | PASS | | 08 §6 |
| NOT-03 | Email / Zalo / SMS | — | — | — | — | — | MISSING | Không có kênh ngoài | — |

## M27 — Dashboard

| ID | Chức năng | Vai trò | UI | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|
| DSH-01 | Dashboard theo vai trò | SA/TO/AD | `/dashboard` | PARTIAL | Chỉ là **thẻ liên kết điều hướng**, không có số liệu | 09 §7 |
| DSH-02 | Trang tổng quan sức khoẻ hệ thống | SA/TO/AD | `/admin/health` | UNVERIFIED·0 | | — |

## M28 — Báo cáo

| ID | Chức năng | Vai trò | UI | API | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|
| RPT-01 | Báo cáo tuần ban giám đốc (tiền thu, công nợ, quá hạn, chi phí theo phòng) | TO | **không có UI** | `GET /tenant/reports/weekly-summary`; tool `get_weekly_summary_report` | PARTIAL | Chỉ qua API / chat |
| RPT-02 | Trang "Báo cáo" tenant / phòng ban | TO/AD | `/tenant/reports`, `/department/reports` | — | **BROKEN** | Hai trang chỉ là bí danh của trang nhật ký Agent (`export { default } from "../../admin/audit/page"`) |
| RPT-03 | Báo cáo trạng thái chứng từ | AD/US | `/admin/voucher-status` | `GET /vouchers/status-report` | PASS | Live: như ACC-12 |
| RPT-04 | Báo cáo tài chính | — | xem ACC-09 | | PARTIAL | |

## M29 — Nhật ký kiểm toán (Audit Log)

| ID | Chức năng | Vai trò | UI | API | Backend / bảng | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|---|
| AUD-01 | Ghi nhật ký mọi request ghi dữ liệu của Gateway | Hệ thống | — | middleware | `Audit/AuditMiddleware.cs` → `audit_log` (2.711 dòng) | — | PARTIAL | **Không có API/UI đọc**; bảng không bất biến (append-only đã bỏ cùng Phase 0) | — |
| AUD-02 | Nhật ký hoạt động Agent (tool, chat, chi phí) theo phạm vi vai trò | SA (toàn bộ), TO (tenant), AD (phòng) | `/admin/audit` (+ 6 bí danh) | Agent `GET /admin/audit` | Redis, giới hạn 5.000 bản ghi | — | PARTIAL | Chỉ lưu tên tool, không lưu đầu vào/đầu ra; bị cắt khi đủ 5.000 | 09 §8 |
| AUD-03 | Lịch sử sự kiện chứng từ / xác thực HĐ / kỳ kế toán | AD/US | `/vouchers/[id]` | `/vouchers/{id}/events`, tax-verification events, `/accounting-periods/{p}/events` | bảng `*_events` | (IT) | UNVERIFIED·IT | | 10 §1 |

## M30 — Usage & chi phí

| ID | Chức năng | Vai trò | UI | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|
| USG-01 | Ghi usage token/chi phí | Hệ thống | — | PASS | (RUN-04) | — |
| USG-02 | Trang Usage tenant / nền tảng | TO/SA | `/tenant/usage`, `/system/usage` | **BROKEN** | Bí danh của trang nhật ký Agent; không có báo cáo usage riêng | — |
| USG-03 | Hạn mức tenant | SA | (PLT-03) | PASS | | 09 §2 |

## M31 — Cấu hình hệ thống

| ID | Chức năng | TT | Thiếu sót / rủi ro |
|---|---|---|---|
| CFG-01 | Cấu hình qua `.env` / appsettings | PASS | Gateway live đang chạy `ASPNETCORE_ENVIRONMENT=Development` |
| CFG-02 | Cấu hình model/provider, cài đặt nền tảng qua UI | MISSING | (PLT-11, PLT-12) |
| CFG-03 | Cấu hình validator theo tenant (trường bắt buộc, ngưỡng tin cậy) | PARTIAL | Bảng `tenant_voucher_settings`, không có UI |

## M32 — Tích hợp

| ID | Chức năng | TT | Thiếu sót / rủi ro |
|---|---|---|---|
| INT-01 | MISA / Fast / Bravo / ERP | MISSING | Chỉ có dòng chữ "khi triển khai thật…" ở trang NCC |
| INT-02 | Cổng tra cứu HĐĐT TCT | PARTIAL | (VOU-07) |
| INT-03 | Nhập sổ Excel từ URL (`import_excel_ledger`) | PARTIAL | Chỉ đọc & xem trước 200 dòng, có chống SSRF |
| INT-04 | Clerk SSO | UNVERIFIED·0 | (AUTH-03) |
| INT-05 | Đọc URL công khai (`fetch_url`) có chống SSRF | PASS | `test_fetch_url_ssrf.py`, `test_ssrf_protection.py` |

## M33 — Giám sát & quan sát

| ID | Chức năng | TT | Bằng chứng / rủi ro |
|---|---|---|---|
| MON-01 | Health check Gateway (`/healthz/live`, `/healthz/ready`) | PASS | 200, Postgres ok (kiểm tra 2026-11-18/19) |
| MON-02 | Metrics Gateway → Prometheus | PASS | target `aiq-gateway` up |
| MON-03 | Metrics Agent → Prometheus | **BROKEN** | target `aiq-agent` **down — HTTP 404** trên `/metrics` |
| MON-04 | Log tập trung (Loki + Promtail), Grafana | UNVERIFIED·0 | Container chạy; chưa kiểm tra dashboard |
| MON-05 | Hangfire dashboard | PARTIAL | `/hangfire` 404 trên cổng 8080 (đường dẫn cấu hình khác hoặc tắt) |
| MON-06 | OpenAPI (môi trường dev) | BROKEN | `/openapi/v1.json` trả 500 |

## Module phát hiện thêm

### M34 — Hợp đồng

| ID | Chức năng | Vai trò | UI | API | Test | TT | Thiếu sót / rủi ro | HD |
|---|---|---|---|---|---|---|---|---|
| CON-01 | Tải & trích xuất hợp đồng (nhiều tài liệu) | AD/US | `/contracts/upload` | `/contracts/*`, `/contracts/{id}/documents` | `test_contract_*`, `ContractProcessingIntegrationTests` (IT) | PASS | | 10 §6 |
| CON-02 | Duyệt / bàn giao hợp đồng (chỉ ADMIN, qua HITL) | AD | `/contracts/[id]` | `/contracts/{id}/review`, `/handoff` | `TriggerRuleEvaluatorTests` | PASS | Kiểm tra quyền ADMIN trong handler (`ContractEndpoints.cs:55-61`) | 10 §6 |
| CON-03 | Checklist nghĩa vụ hợp đồng + nhắc hạn | AD/US | `/contracts/[id]` | `/contracts/{id}/checklist`, `/checklist/*` | `ContractDeadlineJob` | UNVERIFIED·IT | | 10 §6 |
| CON-04 | Lịch thanh toán theo hợp đồng, đối chiếu với thu tiền | Hệ thống | `/contracts/[id]` | | `contract-schedule.test.ts`, `CashSettlementIntegrationTests.ContractSchedules` (IT) | PASS | | 10 §6 |
| CON-05 | Phạt trả chậm theo kỳ | Hệ thống | | | `ContractInstallmentPenaltyJobTests` | PASS | | 10 §6 |

### M35 — Bảo vệ dữ liệu cá nhân (DLP)

| ID | Chức năng | Vai trò | UI | TT | Bằng chứng |
|---|---|---|---|---|---|
| DLP-01 | Che PII (CCCD, SĐT, email, MST, tài khoản NH, thẻ, hộ chiếu, ngày sinh) trước khi gửi LLM | Hệ thống | — | PARTIAL | **Đang tắt trên môi trường hiện tại** (`appsettings.json` `Dlp:Enabled=false`, không có env ghi đè, `sim_dlp_events` = 0) dù trang DLP khẳng định PII luôn được che; chat đi thẳng web → Agent nên **không qua lớp DLP** kể cả khi bật |
| DLP-02 | Thống kê DLP | TO | `/admin/dlp` | PASS | `GET /dlp/summary`; Live: TO mở `/admin/dlp` — hiển thị 0 sự kiện vì DLP đang tắt (DLP-01) |

### M36 — Hồ sơ công ty dùng trong Agent

| ID | Chức năng | TT | Bằng chứng |
|---|---|---|---|
| CTX-01 | Agent nhận danh tính công ty từ server (không từ người dùng) | PASS | `registry.py fetch_company_context_block`, `test_runtime_security.py`. Live: từ chối truy cập tenant khác dù người dùng tự nhận là quản trị (C06) |

---

## Tổng hợp (sau kiểm tra live 2026-11-19)

| Trạng thái | Số chức năng |
|---|---|
| PASS | 74 |
| PARTIAL | 33 |
| MISSING | 11 |
| BROKEN | 7 |
| UNVERIFIED·IT (có integration test, chưa chạy) | 21 |
| UNVERIFIED·0 (không có test tự động, chưa kiểm tra live) | 28 |
| **Tổng** | **174** |

36 module M01–M36 (33 theo danh sách yêu cầu, trong đó M21–M23 thuế gộp một bảng; thêm Hợp đồng, DLP, Hồ sơ công ty trong Agent). Đếm bằng script từ các dòng bảng. Trước kiểm tra live: PASS 60 · UNVERIFIED 63; kiểm tra live (4 vai trò, 92 lượt mở trang, 15 lượt chat — [02 §Live](02_FUNCTIONAL_AUDIT_REPORT.md)) nâng 15 chức năng lên PASS (một trong số đó, TEN-03, từ PARTIAL), hạ DLP-01 từ PASS xuống PARTIAL và bổ sung CHAT-06.
