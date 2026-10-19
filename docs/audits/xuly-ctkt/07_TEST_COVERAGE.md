# 07 — Test Coverage

## 1. Môi trường chạy test trong lần audit (không đụng live)

| Thành phần | Cách chạy | Cô lập | Kết quả |
|---|---|---|---|
| Gateway (.NET 9, xUnit, serial) | `mcr.microsoft.com/dotnet/sdk:9.0`, 93 lát `--filter` (theo class / prefix) | Postgres **riêng** `aiq-audit-pg` (image `pgvector/pgvector:pg16` giống live, tmpfs, mạng riêng `aiq-audit-net`). Khởi tạo bằng `infra/postgres/init/*.sql` + 65 migration của repo ⇒ 101 bảng public (= live). **Không** kết nối DB live. | **822 test duy nhất — 822 PASS, 0 FAIL, 0 SKIP** (824 lượt chạy; 2 test bị chạy hai lần do prefix `Term`/`Terms`, `Receipt`/`Receipt_date` chồng nhau) |
| Agent (Python, pytest) | image Agent hiện tại, `--network none` | Không mạng, không DB | **344 passed, 8 skipped** |
| Web (Next.js) | `node:20`: `tsc --noEmit`, `vitest run` | Không DB | tsc **0 lỗi**; vitest **95/95 PASS** (14 file) |

Bằng chứng test thực sự chạy (không phải "pass rỗng" do thiếu DB):

- DB cô lập ghi nhận `xact_commit = 18,720`, `tup_inserted = 605,638` sau khi chạy.
- Các schema / database tạm đã được dọn (0 schema thừa, 0 database `aiq_%` còn lại).

Lý do chọn DB cô lập: nhiều fixture tạo **schema tạm bên trong database được cấu hình**. Nếu chạy với cấu hình mặc định, test sẽ ghi vào DB live `aiq`, vi phạm quy tắc audit.

## 2. Kết quả theo nhóm (lượt chạy PASS)

| Nhóm | Test class (số test PASS) |
|---|---|
| Tiếp nhận / phân loại / trích xuất | `VoucherTypeClassifierTests` (34), `DocumentProcessingUnitTests` (32), `DocumentProcessingIntegrationTests` (9), `DocumentExtractionTenantIsolationTests` (7), `DocumentGateUnitTests` (29), `DocumentGateHardeningIntegrationTests` (17), `RuntimeConfigurationUnitTests` (24), `RuntimeConfigurationIntegrationTests` (15), `TenantStorageTests` (4), `AgentClientTests` (3), `AgentCallResultTests` (6) |
| Phiếu thu / sao kê / công nợ | `CashSettlementIntegrationTests.*` (~120 qua các lát), `CashSettlementRulesTests` (50), `BankStatementParserTests` (22), `CounterpartyMatcherTests` (15), `ArApOpenItemIntegrationTests` (17), `InvoiceDirectionResolverTests` (9), `JournalRoutingRulesTests` (14), `AccountsReceivableOverdueJobTests` (1) |
| Chiết khấu HĐ | `PaymentDiscountCalculatorTests` (14), `PayinCalculatorTests` (16), `CashSettlementIntegrationTests.{Term,Terms,Invalid,Full_,Deadline,Late_,No_contract,Only_,Disabled,Wrong_,Currency,Partial_,Overpayment_,Stale,Concurrent_,Extracted,Receipt_date}` |
| Hợp đồng | `ContractProcessingIntegrationTests` (11), `CashSettlementIntegrationTests.Schedule*` (18), `ContractInstallmentPenaltyJobTests` (9) |
| Đề nghị thanh toán | `ExpenseClaimRecognitionIntegrationTests` (18), `ExpenseClaimPaymentIntegrationTests` (18), `ExpenseClaimMultiFileIntegrationTests` (17), `ExpenseClaimItemRulesTests` (18), `ExpenseClaimSameAdminLifecycleTests` (3), `TaxVerificationIntegrationTests` (33) |
| ACL / tenant | `AdminDepartmentAuthorityInvariantTests` (13), `CashSettlementEndpointPolicyTests` (7), `BankingEndpointPolicyTests` (1), `TaxVerificationEndpointPolicyTests` (6), `PrincipalAuthorizationPolicyTests` (5), `DepartmentRbacIntegrationTests` (19), `AclV2*` (4), `DepartmentAccessPolicyTests` (5) và các test governance khác |

Bảng đầy đủ theo class: chạy lại theo mục 5.

## 3. Mức xác minh theo requirement

| Mức | Requirement |
|---|---|
| TEST VERIFIED (có test chạy PASS trong audit) | SC-02, SC-03, SC-04, CF-01..05, PR-01, PR-03, PR-04, PR-05, AR-01..07, EX-01..03, SC-01 / HD-01 (phần đã triển khai) |
| STATIC VERIFIED (chỉ đọc code) | PR-02 (vắng mặt), AR-08 (vắng mặt), PR-06 (phần lớn), EX-04 |
| LIVE VERIFIED | **Không có trong lần audit này** |
| NOT VERIFIED | Không có requirement nào hoàn toàn không có bằng chứng |

### Vì sao không có LIVE VERIFIED (BLOCKED)

- Mọi luồng nghiệp vụ trên môi trường thật đều cần đăng nhập. Tài khoản kiểm thử không có mật khẩu đã biết, và quy tắc audit cấm đổi mật khẩu.
- Mọi luồng đều ghi dữ liệu, mà quy tắc cấm ghi DB live.
- Trên DB live chỉ thực hiện **truy vấn đọc**: đếm bảng, `tenants.company_tax_id`. Hồ sơ LIVE-TEST còn lưu từ các đợt kiểm thử trước **không** được dùng làm bằng chứng cho audit này.

## 4. Test còn thiếu (đề xuất)

| Thiếu test cho | Liên quan |
|---|---|
| Trích xuất lại / `apply-extraction` trên phiếu thu / sao kê / hoá đơn **đã ghi sổ** (hiện không có guard ⇒ test sẽ ghi lại hành vi lỗi) | R-01, AG-01 |
| Đối chiếu tổng nhật ký ghi sổ ↔ số dư `current_ar` / `current_ap` / ngân hàng | R-02, R-03 |
| Thông báo (in-app) sinh ra cho từng sự kiện đề nghị thanh toán / phiếu thu | PR-06, EX-04 |
| E2E trên trình duyệt chạy tự động (hiện chỉ có script Playwright thủ công ngoài repo) | tất cả luồng |
| Hiệu năng / độ ổn định OCR khi tải nhiều file | AG-02 |
| Agent pytest: 8 test bị skip (cần môi trường đặc thù) | Agent |

## 5. Tái lập kết quả

1. Khởi tạo Postgres cô lập từ `infra/postgres/init` + `gateway/src/Aiq.Gateway/Data/Migrations` (thứ tự tên file, mỗi migration một transaction).
2. Chạy `dotnet test` với `POSTGRES_HOST`, `POSTGRES_PASSWORD`, `POSTGRES_USER`, `POSTGRES_DB` trỏ tới DB cô lập, `--settings tests/Aiq.Gateway.Tests/serial.runsettings`, theo từng `--filter` class.
3. Agent: `python -m pytest tests` trong image Agent, `--network none`.
4. Web: `npx tsc --noEmit -p .`, `npx vitest run`.
