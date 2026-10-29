# Tiến độ chứng từ flow v2

> **Khi resume:** chỉ đọc file này, rồi làm tiếp từ bước `⏳`/`[ ]` đầu tiên của PR đang làm.
> Không cần đọc lại chung-tu-current.md / chung-tu-proposed.md trừ khi bước đó ghi rõ mục cần đọc.
> Sau mỗi bước xong: đánh `[x]`, ghi ngắn kết quả + file đã đụng, commit cùng code của bước đó.

Branch: `feat/chung-tu-flow-v2` · Kế hoạch: `docs/flows/chung-tu-proposed.md` §9 · Quyết định: §10

## Cách chạy test (không có dotnet trên host)

```bash
# Postgres tạm (không đụng aiq-postgres)
docker network create aiq-test-net
docker run -d --name aiq-test-pg --network aiq-test-net -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=testpass \
  -e POSTGRES_DB=aiq_gw_test pgvector/pgvector:pg16 -c max_connections=1000
# Gateway (repo root)
docker run --rm --network aiq-test-net -e AIQ_TEST_POSTGRES="Host=aiq-test-pg;Port=5432;Database=aiq_gw_test;Username=postgres;Password=testpass" \
  -v aiq-gw-nuget-cache:/root/.nuget/packages -v "$PWD":/src -w /src/gateway mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "dotnet build --configuration Release -warnaserror && dotnet test tests/Aiq.Gateway.Tests/Aiq.Gateway.Tests.csproj --configuration Release --no-build --settings tests/Aiq.Gateway.Tests/serial.runsettings"
# Agent (thư mục agent/)
docker run --rm -v "$PWD":/app -w /app -e PYTHONPATH=/app/src --entrypoint sh aiq-agent-pytest:latest -c \
  "pip install -q --target /tmp/r ruff==0.17.2; PYTHONPATH=/tmp/r python -m ruff check src tests; python -m pytest -q -p no:cacheprovider"
# Web (thư mục web/)
npx tsc --noEmit && npm test
# Dọn
docker rm -f aiq-test-pg; docker network rm aiq-test-net
```

Test DB-backed tự bỏ qua (pass rỗng) nếu thiếu `AIQ_TEST_POSTGRES` — kiểm tra log Postgres có `CREATE DATABASE`.

## PR1 — Guard an toàn ✅ (commit `addd106`, user duyệt 2026-10-29)

## PR2 — Validator ✅ (user duyệt 2026-10-29, sửa tiếp ở PR2.x)

Phạm vi (§9 + QĐ 8, 9, 10, 11): validator thuần, chỉ tham khảo, chưa chặn. Migration 068 + down. `GET /vouchers/{id}/validation`.

- [x] 1. Đọc ngữ cảnh: dạng `vat_rate` trong DB (KCT/KKKNT?), hồ sơ công ty (MST), bảng journal suggestions, style migration 066
- [x] 2. Test unit `VoucherValidator` (viết trước, chạy đỏ)
- [x] 3. Cài `VoucherValidator` + `VatRateTable` + `MstRules` (xanh)
- [x] 4. Migration `068_voucher_validation.sql` + `down/068_voucher_validation.down.sql` + test up→down→up
- [x] 5. Repository (đọc `vat_rates`, `tenant_voucher_settings`, lưu `validation`) + `GET /vouchers/{id}/validation` + test tích hợp
- [x] 6. Chạy toàn bộ test (gateway, agent, web, ruff, tsc), commit, báo cáo PR2 (tóm tắt + test tay)

### Ghi chú PR2 (cập nhật dần)
- Bước 1: `vouchers.vat_rate` NUMERIC(5,2) = phần trăm (0/5/8/10), NULL khi nhiều mức (`infra/postgres/init/18_vouchers.sql:40`); KCT/KKKNT **không có cách biểu diễn** → bảng `vat_rates` có `rate NULL` cho KCT/KKKNT, validator chỉ so số.
- Input validator = `VoucherDetail` (`Vouchers/VoucherRecords.cs`, init props → dễ dựng trong test). Type constants: `VoucherTypes` (`Vouchers/VoucherTypeClassifier.cs:26-35`). MST công ty: `ITenantCompanyProfileRepository.GetAsync(...).TaxCode`.
- Migrator chỉ đọc `Data/Migrations/*.sql` cấp gốc (`DatabaseMigrator.cs:40`, csproj:45) → `down/` an toàn, không bị copy ra output; test đọc down qua `TaxVerificationDatabase.RepoPath`-style.
- Thiết kế: namespace `Aiq.Gateway.Vouchers.Validation`; `VoucherValidator.Validate(VoucherDetail, VoucherValidationContext)` thuần; `MstRules`; issue `{Code, Severity, Field(snake_case), Message}`.
- Ngoài PR2 (ghi rõ trong báo cáo): PERIOD_OPEN (PR4), DUPLICATE v2 (PR7), JOURNAL_ACCOUNT theo bảng TK (PR8, PR2 chỉ kiểm định dạng = warning), AMOUNT_IN_WORDS (sau).
- Bước 2: `tests/Aiq.Gateway.Tests/Vouchers/VoucherValidatorTests.cs` (pure, ~45 case). MST checksum: trọng số 31,29,23,19,17,13,7,5,3; d10 = 10 − (Σ mod 11), kết quả 10 = sai (thuật toán [CHỜ xác nhận] → chỉ warning). MST mẫu hợp lệ: 0312345673, 0101243150, 0100109106.
- Bước 3: `gateway/src/Aiq.Gateway/Vouchers/Validation/{VoucherValidator,MstRules}.cs` (không cần class `VatRateTable` riêng — `VatRateEntry` + list trong context). 52/52 unit xanh. Bẫy: app chạy globalization-invariant → **không dùng `CultureInfo("vi-VN")`** (throw); dùng `NumberFormatInfo` tự tạo. Mã lỗi đã cài: REQUIRED_FIELD, MST_FORMAT, MST_CHECKSUM(w), COMPANY_TAX_CODE_MISSING(w), BUYER_IS_COMPANY, SELLER_IS_COMPANY, AMOUNT_SUM, LINE_SUM, LINE_AMOUNT(w), VAT_LINE(w), VAT_AMOUNT(w), VAT_RATE, VAT_RATE_TABLE_EMPTY(w), DATE_FUTURE, JOURNAL_BALANCE, JOURNAL_ACCOUNT(w), JOURNAL_TOTAL, LOW_CONFIDENCE(w). Trường bắt buộc mặc định cho thu/chi (số, ngày, số tiền) và expense_claim (ngày, số tiền) là **[GIẢ ĐỊNH]** — QĐ 8 chỉ nêu danh sách cho hoá đơn.
- Bước 4: `068_voucher_validation.sql` tạo `vat_rates` (+seed 0/5/10/KCT/KKKNT từ 2009 "chờ kế toán duyệt", 8% 01/07/2025–31/12/2026 NĐ 174/2025; **không** seed các đợt 8% trước 07/2025), `tenant_voucher_settings`, `voucher_validations`. **Lệch tài liệu (cố ý):** kết quả lưu ở bảng riêng `voucher_validations`, không phải cột `vouchers.validation` (cột sẽ kích trigger `vouchers_touch_updated_at` + đụng chứng từ đã ghi sổ); `field_confidence`/`extraction_source` dời sang PR9/PR11 (backfill vouchers sẽ đụng trigger 059). Down: `down/068_voucher_validation.down.sql` (backup `*_backup_<ts>` rồi drop, xoá dòng schema_migrations). Test `Data/VoucherValidationMigrationTests.cs` xanh. Bẫy: `schema_migrations.checksum` NOT NULL + regex 64 hex.
- Bước 5: `Vouchers/Validation/VoucherValidationService.cs` (repo `IVoucherValidationRepository` + service; "hôm nay" = UTC+7, không cần tzdata; `TimeProvider` đăng ký ở `Program.cs`). Route `GET /vouchers/{id}/validation` → `VoucherEndpoints.ValidationAsync` (TryScope: USER/ADMIN cùng phòng ban; khác phòng ban 404; TENANT_OWNER/SUPER_ADMIN 403). Mỗi lần GET tính lại + upsert `voucher_validations`, không ghi vào `vouchers`. Test `Data/VoucherValidationIntegrationTests.cs` xanh.
- Bước 6: gateway Release `-warnaserror` + full suite serial **969/969**; agent ruff sạch, pytest 407 pass / 8 skip; web tsc sạch, vitest 102/102. Đã đồng bộ `chung-tu-proposed.md` (§3.3, §4.1, §9). Container `aiq-test-pg` + network `aiq-test-net` đã xoá.

## PR2.x — Sửa sau review PR2 ✅ (push `38f6404`; user 2026-10-29: chỉ test local, không cần kiểm CI)

Yêu cầu user (2026-10-29): (1) GET validation không ghi DB, chỉ lưu khi chứng từ đổi + khi submit (PR3), thêm snapshot phiên bản chứng từ (migration mới + down); (2) `JOURNAL_TOTAL` → warning vĩnh viễn, thêm ERROR tổng Nợ = tổng Có; (3) "hôm nay" theo Asia/Ho_Chi_Minh, cho phép lệch 1 ngày, test 23:30 / 00:30 giờ VN; (4) test VAT_RATE dùng vat_rates riêng, không dựa seed 068; (5) ghi mục chờ người dùng.

- [x] 1. Validator: `JOURNAL_TOTAL` warning; `JOURNAL_BALANCE` = tổng Nợ = tổng Có (error), kiểm từng dòng đổi tên `JOURNAL_LINE`; `DATE_FUTURE` > hôm nay VN + 1 ngày; `Version` = 2; test unit trước
- [x] 2. Migration `069_voucher_validation_snapshot.sql` (+ down) thêm `voucher_updated_at`, `voucher_fingerprint` vào `voucher_validations`; test up→down→up
- [x] 3. Service tách `ComputeAsync` (GET, không ghi) / `RecordAsync` (lưu); gọi Record sau tạo / PATCH / apply-extraction / job trích xuất (upload + reextract) / bút toán gợi ý / đổi loại; test tích hợp
- [x] 4. Test tích hợp VAT_RATE với bảng `vat_rates` của riêng test (các đợt 8% không liên tục)
- [x] 5. Chạy đủ test, đồng bộ tài liệu, commit, push, kiểm tra GitHub Actions (`gh run list --branch feat/chung-tu-flow-v2 --limit 3`); đỏ thì sửa

### Ghi chú PR2.x
- Bước 1: `VoucherValidator.cs` (Version 2, `FutureDateToleranceDays = 1`), `VoucherValidationService.TodayInVietnam` dùng tz `Asia/Ho_Chi_Minh` (fallback UTC+7 cố định). Mã mới `JOURNAL_LINE` (từng dòng, error); `JOURNAL_BALANCE` giờ là tổng Nợ = tổng Có (error) — với cấu trúc cặp hiện tại chỉ lệch khi dòng thiếu TK; `JOURNAL_TOTAL` warning. Test unit 61/61.
- Bước 2: `069_voucher_validation_snapshot.sql` thêm `voucher_updated_at TIMESTAMPTZ`, `voucher_fingerprint TEXT` (CHECK 64 hex), NULL = dòng cũ → coi là cũ. Down chỉ drop 2 cột (dữ liệu dẫn xuất, không backup), giữ kết quả. Test 068 "up again" giờ chạy 068+ để 069 không mất cột. 3/3 xanh.
- Bước 3: `VoucherValidationService` → `ComputeAsync` (GET, không ghi), `RecordAsync` (lưu + snapshot), `TryRecordAsync` (sau thay đổi; lỗi chỉ log vì thay đổi đã commit — dòng thiếu/cũ tự lộ qua snapshot). Report = `VoucherValidationReport` (thay `StoredVoucherValidation`). Fingerprint = SHA-256 JSON {voucher, line items, journal}. Hook: `CreateAsync`, `UpdateAsync`, `ApplyExtractionAsync`, `SaveJournalAsync`, `DecideJournalAsync`, `SetVoucherTypeAsync`, `VoucherExtractJobRunner` (sau khi ghi field + journal → upload và reextract). `UpdateAsync`/`DecideJournalAsync` đổi sang internal cho test. Job runner thêm tham số ctor (sửa 2 test dựng runner). Mutation check: GET ghi lại + bỏ hook create → 5 test đỏ.
- Bước 4: `VoucherValidationIntegrationTests` dùng `IAsyncLifetime` thay bảng `vat_rates` bằng dữ liệu riêng (3 đợt 8% có khoảng trống) trước mỗi test; Theory 5 ngày (trong/ngoài đợt). 116 test liên quan xanh.
- Bước 5: full suite — gateway Release `-warnaserror` serial **987/987**; agent ruff sạch, pytest 407 pass / 8 skip; web tsc sạch, vitest 102/102. Đồng bộ `chung-tu-proposed.md` (§3.3 bảng quy tắc, §4.1 thêm 069, §5 GET chỉ đọc, §9 PR2). **Đánh lại số migration dự kiến**: lifecycle 067 → **070** (067 tạo sau 068/069 sẽ chạy lệch thứ tự giữa DB mới và DB đã migrate), accounting_periods → 071, chart_of_accounts → 072, posting_integrity → 073, dedup_v2 → 074.
- CI: `ci.yml` chỉ chạy khi push `master`, PR vào `master` hoặc `workflow_dispatch` — push branch **không** kích CI. Máy không có `gh`.
- "lệch 1 ngày": user xác nhận là quyết định của họ ngày 29/10 → **QĐ 25** trong §10.

> ⛔ **KHÔNG bật `voucher_flow_v2` cho tenant thật trước khi xong PR5** (user 2026-10-29). Trước PR5 tenant v2 chưa có `post`/`reverse`, gate L2, `If-Match`; `apply-invoice`/`apply-expense` vẫn ghi `status='verified'` bỏ qua vòng đời.

## Đang chờ người dùng / kế toán
- Review PR3 + PR3.x. Trước khi bật cờ cho tenant nào: chạy `scripts/dryrun-migrations.sh <dump>` trên bản dump DB thật (user đưa file).
- Cấu hình `Security__AgentInternalNetworks` (IP/mạng riêng của agent) trên server — user hoãn (2026-10-29); đến lúc đó nhãn audit agent hiển thị là user.
- Kế toán bổ sung các đợt giảm thuế GTGT 8% trước 07/2025 theo từng nghị quyết/nghị định (thêm dòng `vat_rates`, không sửa code).
- Xác nhận trường bắt buộc cho phiếu thu/chi (`document_date`, `document_number`, `total_amount`) và đề nghị thanh toán (`document_date`, `total_amount`) — hiện là [GIẢ ĐỊNH].
- QĐ 8 (danh sách trường bắt buộc chính thức), QĐ 14 (bảng TK TT99/TT133 + ánh xạ, trước PR8), thuật toán checksum MST.

## PR3 — Vòng đời + audit ✅ (review 2026-10-29 → sửa ở PR3.x)

Phạm vi (§2, §3.7, §4.1 `070`, §4.2, §5, §6.3, QĐ 1, 2, 3, 20, 23, 24): chỉ test local (không kiểm CI).
Ngoài PR3: `post`/`reverse`/`{post:true}`/alias `apply-*` + gate L2 + `If-Match` (PR5), kỳ kế toán (PR4), `supersede` (PR6), unique dedup bỏ qua xoá mềm (PR7).

- [x] 1. QĐ 25 vào §10; ghi nhớ "chỉ test local"; kế hoạch PR3
- [x] 2. `VoucherStateMachine` thuần + test ma trận trạng thái × hành động × vai trò (ADMIN tự duyệt chứng từ của mình; `reversed`/`rejected` cuối; agent bị từ chối)
- [x] 3. Migration `070_voucher_lifecycle.sql` + down: cột vòng đời, `voucher_events` append-only, `tenant_feature_flags`, trigger đồng bộ `status`→`lifecycle_state` (+ `lifecycle_version`, `posted_at`), trigger ghi event (actor từ `set_config`), backfill (không đổi `updated_at`), chuyển `journalized`; test up→down→up
- [x] 4. Repository vòng đời (chuyển trạng thái nguyên tử + event cùng transaction, đọc events, cờ tenant) + guard `voucher_locked` trên các đường sửa (PATCH, apply-extraction, reextract, job, bút toán, đổi loại) + thông báo agent/web
- [x] 5. Validator → vòng đời (khi bật cờ): `extracted`/`needs_fix`/`validated` theo kết quả
- [x] 6. Endpoint `submit`/`approve`/`return`/`reject` + `GET /events`, theo cờ `voucher_flow_v2`; test ACL (ADMIN tự duyệt OK, USER không duyệt, TO/SA 403, khác phòng 404, header agent bị từ chối)
- [x] 7. Xoá mềm (khi bật cờ) + lọc `deleted_at` ở các đường đọc + chặn xoá chứng từ đã từng ghi sổ
- [x] 8. Middleware xoá `X-AIQ-Actor` (QĐ 23) + `Security:AgentInternalNetworks` + test 4 trường hợp
- [x] 9. Bỏ nhánh `journalized` trong code; chạy đủ test; đồng bộ tài liệu; commit; báo cáo

### Ghi chú PR3
- Bước 2: `Vouchers/Lifecycle/VoucherStateMachine.cs` (`LifecycleStates`, `LifecycleAction`, `LifecycleActor(Kind, IsCreator)`, `Decide` → `LifecycleDecision`; lỗi `forbidden_actor` → kiểm trước, rồi `invalid_transition`, rồi `reason_required`). `IsEditable`, `IsDeletable(state, everPosted)`, `IsTerminal`. Thêm `extracted → rejected` (sơ đồ §2.2 không vẽ nhưng `extracted` có thể đứng yên khi cờ vừa bật). Test `Vouchers/VoucherStateMachineTests.cs` 676/676 (ma trận 9 × 9 × 8 + lý do + workflow ADMIN tự làm + trạng thái cuối).
- Bước 3: `070_voucher_lifecycle.sql` + `down/070_voucher_lifecycle.down.sql`. Trigger BEFORE `trg_vouchers_lifecycle_sync` (v2 ghi `lifecycle_state` → mirror `status` qua `voucher_status_from_lifecycle`; đường cũ ghi `status` → map qua `voucher_lifecycle_from_status`; tăng `lifecycle_version`; `posted_at` = lần ghi sổ đầu, không xoá). Trigger AFTER `trg_vouchers_lifecycle_event` ghi `state_change` (cả INSERT). `vouchers_touch_updated_at` nhận `aiq.backfill=on` để backfill không đổi `updated_at`. Backfill: `journalized` có bằng chứng (posting/AR/AP/cash posted) → verified/posted, không → extracted/needs_fix, ghi `legacy_backfill`; thu/chi đã huỷ kiểu cũ → `posted_at` = lần post đầu + `legacy_backfill` (test thêm ở PR3.x). Down khôi phục `journalized` từ event. Bẫy: tên backup trùng khi chạy down 2 lần/giây → hậu tố `clock_timestamp()` + micro giây (sửa cả down 068). Test `Data/VoucherLifecycleMigrationTests.cs` 6/6; full suite 1668 pass trước khi sửa hậu tố.
- Bước 4: `Vouchers/Lifecycle/{VoucherLifecycleGuard,TenantFeatureFlags,VoucherLifecycleRepository}.cs`; `VoucherRecord` + `VoucherSelectColumns` thêm cột vòng đời. Guard SQL `NotLockedSql` (pending_approval/approved/reversed/đã xoá mềm) ở ReplaceExtracted, UpdateOcrResult, Update (PATCH, thêm cả NotPosted), Delete (+ `posted_at IS NULL`), AcceptJournal, SetVoucherTypeClassification; SaveJournalSuggestions khoá dòng `FOR UPDATE` rồi ném `VoucherLockedException`. Endpoint kiểm trước + `EditRefusedAsync` chọn đúng mã khi repo từ chối (race). Job trích xuất bỏ qua chứng từ bị khoá. **Lỗ hổng cũ đã bịt:** PATCH đặt `status='verified'` (= tự ghi sổ không qua gate) → 400 `status_not_editable` cho mọi tenant; bật cờ v2 thì PATCH không đổi status nào. Agent `_conflict_text` xử lý `voucher_locked` (+ test). Test `Data/VoucherLifecycleIntegrationTests.cs`.
- Bước 5: `VoucherValidationService.RecordAsync` (ctor thêm `ITenantFeatureFlags`, `IVoucherLifecycleRepository`): cờ bật + trạng thái thuộc extracted/needs_fix/validated → `VoucherStateMachine` (actor system) quyết `validated`/`needs_fix`, chuyển rồi tính lại snapshot sau khi chuyển. Cờ tắt hoặc trạng thái khác: không động vào vòng đời. 5 test mới.
- Bước 6: `Endpoints/VoucherLifecycleEndpoints.cs` (`MapVoucherLifecycle` trong nhóm `/vouchers`): `GET /{id}/events`, `POST /{id}/submit` (mọi thành viên phòng ban; state machine quyết), `approve`/`return`/`reject` (`department_admin`). Thứ tự: TryScope (TO/SA 403) → cờ (409 `voucher_flow_v2_disabled`) → `post:true` 400 (PR5) → tìm theo phòng ban (404) → `MayAct` (403 `forbidden_actor`, trước khi ghi gì) → submit: `RecordAsync` (lưu kết quả lúc submit; lỗi → 409 `validation_failed`, chứng từ về needs_fix) → `Decide` → approve gate (validator không lỗi, loại đã xác nhận, review không chặn, HĐ đầu vào thuế ≠ invalid) → `TransitionAsync`. Return xoá `submitted_*`/`approved_*`. Header `X-AIQ-Actor: agent` → 403. Mutation (bỏ `MayAct` sớm) → 2 test đỏ. 705 test (state machine + tích hợp) xanh.
- Bước 7: `IVoucherLifecycleRepository.SoftDeleteAsync` (UPDATE `deleted_at/by` chỉ khi editable/rejected + `posted_at IS NULL` + NotPosted, event `deleted` cùng transaction). `DELETE /vouchers/{id}`: cờ bật → xoá mềm; cờ tắt → xoá cứng như cũ. Cả hai từ chối chứng từ **đã từng** ghi sổ (`posted_at`) → 409 `voucher_posted` (thay đổi nhỏ cho tenant chưa bật cờ: HĐ đã post rồi unapply kiểu cũ không còn xoá được). Lọc `deleted_at IS NULL`: `FindAsync`, `FindInDepartmentAsync`, `ListInDepartmentAsync`, `ListAsync`, status-report, hàng chờ L2 (`IApprovalRepository`); các câu đọc khác đi theo id sau `FindInDepartmentAsync`. **Hạn chế (để PR7):** chứng từ xoá mềm vẫn giữ khoá trùng `uniq_voucher_document` → tải lại cùng HĐ báo trùng; `FindDuplicateAsync` vẫn thấy nó. File S3 giữ nguyên (QĐ 19). 56 test xanh.
- Bước 8: `Security/AgentActorHeaderMiddleware.cs` + `AgentActorHeaderOptions` (section `Security`, `AgentInternalNetworks` CIDR, mặc định rỗng = luôn xoá), đăng ký **đầu tiên** trong pipeline (`Program.cs`, trước `UseExceptionHandler`; app không dùng `UseForwardedHeaders` nên `RemoteIpAddress` là IP socket). IPv4-mapped IPv6 được map. CIDR sai → lỗi lúc khởi động. Test `Security/AgentActorHeaderMiddlewareTests.cs` 7/7 (ngoài, agent, XFF giả, rỗng, hàng xóm ngoài CIDR, không header, CIDR sai). Route policy: approve/return/reject `department_admin`, submit không (`AdminDepartmentAuthorityInvariantTests`); 2 test policy quét thêm `VoucherLifecycleEndpoints`. **Hệ quả triển khai:** chưa cấu hình CIDR thì nhãn audit `agent` (`VoucherEndpoints.ActorType`) thành `user` — cấu hình mạng/IP agent trên server cần user duyệt riêng (§6.3), chưa đụng.
- Bước 9: bỏ `journalized` khỏi 2 check `voucher_finalized` và bộ lọc/nhãn trên web (`vouchers/page.tsx`); **giữ** trong `VoucherPostingGuard` + PATCH làm lớp phòng thủ (cột `status` không CHECK; trigger cũng map `journalized` → posted). `CashSettlementIntegrationTests` tự dựng schema → thêm cột 070. Full suite: gateway Release `-warnaserror` serial **1717/1717**; agent ruff sạch, pytest **408** pass / 8 skip; web tsc sạch, vitest 102/102. Đồng bộ `chung-tu-proposed.md` (§4.1 070 ✅, §5, §9 PR3 ✅).
- Thiết kế: một trigger AFTER ghi `voucher_events` `state_change` cho MỌI thay đổi `lifecycle_state` (đường cũ = `system`; đường v2 đặt `aiq.actor_id/actor_type/reason` bằng `set_config(..., true)` trong transaction) → một nơi ghi duy nhất, luôn cùng transaction.
- `voucher_events.voucher_id` không FK (lịch sử sống sót khi tenant chưa bật cờ còn xoá cứng); append-only, chỉ cho DELETE khi tenant đã bị xoá (cascade).
- Trigger 059 chỉ chặn khi đổi cột nội dung → cập nhật cột vòng đời không vướng.

## PR3.x — Sửa sau review PR3 ✅ (user cho làm tiếp PR4, 2026-10-29)

User 2026-10-29: đồng ý ADMIN phải `return` rồi mới accept/reject từng dòng bút toán; hoãn `Security__AgentInternalNetworks`; xong 1–7 thì dừng trước PR4.

- [x] 1. Tìm mọi lời gọi PATCH `/vouchers/{id}` có `status` (web, agent, scripts) → **không có**. Nút "Duyệt & ghi nhận" luồng cũ gọi `apply-invoice`/`apply-expense` (ADMIN), các endpoint này tự ghi `status='verified'` bằng `repo.UpdateAsync` trong Gateway, không qua PATCH; thu/chi đi `settlement.PostAsync`. → Không cần `POST /record`. Thêm test hồi quy: cờ tắt, ghi `verified` kiểu cũ vẫn post + có event (`Without_the_flag_the_legacy_recognition_write_still_posts`), vì PR3 thêm guard NotPosted/NotLocked vào `UpdateAsync`.
- [x] 2. Xác nhận backfill `journalized` (QĐ 24): `status` đổi song song — có dấu vết (journal `posting`/AR/AP/cash posted) → `verified` + `posted`; không → `extracted` + `needs_fix`. Test mở rộng đủ 4 loại dấu vết + thu/chi huỷ kiểu cũ (`posted_at`, event `legacy_backfill` mang id giao dịch) + không còn dòng `journalized`.
- [x] 3. Test: lưu kết quả validator (`RecordAsync` + `TryRecordAsync`, có lỗi và không lỗi) khi `draft`/`pending_approval`/`approved`/`posted`/`reversed`/`rejected` → `status`, `lifecycle_state`, `lifecycle_version` không đổi, không thêm event, kết quả vẫn được lưu (12 case).
- [x] 4. `voucher_events`: trigger chặn UPDATE/DELETE đã có trong 070 (23514); thêm trigger statement chặn `TRUNCATE` + test (UPDATE/DELETE/TRUNCATE đều lỗi, dữ liệu còn; xoá tenant vẫn cascade). Sửa thẳng 070 vì chưa DB nào ngoài test chạy 070 (image gateway đang chạy build 07/10, chỉ có migration đến 065).
- [x] 5. 3 bước kiểm trước PR3 — xem ghi chú.
- [x] 6. `scripts/dryrun-migrations.sh <dump> [--until 070]`: restore dump (custom/plain/.gz) vào Postgres tạm (mạng `--internal`, không publish port, không volume) → chạy migration như `DatabaseMigrator` (thứ tự ordinal, 1 transaction/file, checksum SHA-256, từ chối file đã áp dụng bị sửa) → thống kê → xoá container. Không nhận connection string.
- [x] 7. Ghi "KHÔNG bật `voucher_flow_v2` cho tenant thật trước khi xong PR5" (đầu mục Đang chờ).

### Ghi chú PR3.x
- Bước 5 (trung thực): trước PR3 **chưa** build lại image agent (dùng `aiq-agent-pytest` build 21/09) và **chưa** chạy `DatabaseMigrator` thật trên DB trống (chỉ fixture test mô phỏng vòng lặp của nó). `git status` thì đã kiểm (chỉ còn file không phải của mình). PR3.x: build lại `docker build --target test -t aiq-agent-pytest:latest agent/` (wheel từ `pyproject.toml` hiện tại; không tag image runtime đang chạy); test mới `Data/FreshDatabaseMigratorTests.cs` chạy `DatabaseMigrator.ApplyAsync` thật trên DB trống (đủ file, đúng checksum, chạy lại không đổi); `dryrun-migrations.sh` trên dump rỗng: 71 migration xanh.
- Dry-run trên dump mẫu trước 070 (9 chứng từ): journalized 3 → 0 (2 verified/posted, 1 extracted/needs_fix), thu/chi huỷ kiểu cũ 1 (có `posted_at` + event), `lifecycle_state` NULL 0, status lệch lifecycle 0. Cả 3 định dạng dump (custom, plain .gz) chạy được. Bẫy: `docker exec -i` trong vòng lặp ăn stdin của `while read` → đọc danh sách qua fd 3.
- `scripts/test_fresh_database_bootstrap.sh` dùng `aiq-agentic-gateway:latest` (image đang chạy, cũ) → không dùng để kiểm 070; không build lại tag đó.

## PR4 — Kỳ kế toán + khoá kỳ ⏳

Phạm vi (§9, QĐ 12, 13, phương án §10.1 P1–P12 [GIẢ ĐỊNH-PR4]): migration 071 + down, API `/accounting-periods`, kiểm tra kỳ trong DB cùng transaction với thao tác ghi, `PERIOD_LOCKED`, agent dừng khi `period_locked`. Chỉ tenant bật `voucher_flow_v2`. Không UI (PR10). Commit cục bộ mỗi bước, KHÔNG push.

- [x] 1. Kế hoạch + bảng [GIẢ ĐỊNH-PR4] (§10.1); đổi `PERIOD_OPEN` → `PERIOD_LOCKED` ở §3.3
- [x] 2. `AccountingPeriod` thuần (`yyyy-MM`, giờ VN, trước/sau, `ReversalDate` cho PR5) + unit test (23:59 cuối tháng / 00:00 ngày 1 giờ VN)
- [x] 3. Migration `071_accounting_periods.sql` + down: `accounting_periods`, `accounting_period_events` (append-only + TRUNCATE), `accounting_period_assert_open` (khoá dòng `FOR SHARE`), trigger trên `vouchers` + `voucher_journal_suggestions` (chỉ v2; miễn metadata + đảo); test (ranh giới 31/10–01/11, tenant chưa bật cờ không đổi, up→down→up)
- [x] 4. Repository khoá/mở (khoá dòng `FOR UPDATE`, thứ tự M−1, còn chứng từ chờ duyệt, lý do mở lại, cảnh báo M+1, event cùng transaction) + test song song (ghi sổ / submit đúng lúc khoá kỳ)
- [x] 5. API `GET /accounting-periods?year=`, `POST /{yyyy-MM}/close`, `/reopen`, `GET /{yyyy-MM}/events` (ADMIN phòng ban; USER/TO/SA/agent bị chặn; khác phòng ban không thấy) + 409 `period_locked` toàn cục + `accountingDate` khi tạo/PATCH (chỉ v2) + test
- [x] 6. Validator `PERIOD_LOCKED` (error, version 3; chỉ khi bật cờ) + test
- [x] 7. Agent: 409 `period_locked` → báo người dùng, KHÔNG thử lại (+ test); không có tool khoá/mở kỳ; job trích xuất ghi lỗi kỳ khoá
- [ ] 8. Chạy đủ test 3 phần; đồng bộ tài liệu (§4.1 071, §5, §9); báo cáo

### Ghi chú PR4
- Bước 2: `Vouchers/Periods/AccountingPeriod.cs` (`AccountingPeriod(Year, Month)`, `TryParse` `yyyy-MM` 2000–2100, `Of(DateOnly)`, `Of(DateTimeOffset)` theo giờ VN, `Previous`/`Next`, `EffectiveDate`; `VietnamTime` dùng chung, `VoucherValidationService.TodayInVietnam` gọi lại; `AccountingPeriodRules.ReversalDate` cho PR5). Test `Vouchers/AccountingPeriodTests.cs` (23:59 31/10 VN = 16:59Z → 2026-10; 00:00 01/11 VN = 17:00Z 31/10 → 2026-11; qua năm; ngày đảo).
- Bước 3: `071_accounting_periods.sql` + `down/071_accounting_periods.down.sql`. `accounting_period_assert_open(tenant, dept, date)` = INSERT dòng `open` ON CONFLICT DO NOTHING rồi `SELECT … FOR SHARE`; kỳ khoá → `RAISE … USING ERRCODE='AQ001'`, message `period_locked:yyyy-MM` (đọc message, không dùng DETAIL vì Npgsql che DETAIL). Trigger `trg_vouchers_period_lock` (BEFORE INSERT/UPDATE/DELETE, chạy sau `trg_vouchers_lifecycle_sync` theo tên) kiểm kỳ cũ + kỳ mới (khoá theo thứ tự ngày) khi `to_jsonb(NEW) - cột miễn ≠ to_jsonb(OLD) - cột miễn` → cột mới thêm sau này mặc định được bảo vệ. Miễn: posted→reversed (P3). `trg_journal_period_lock` cho bút toán gợi ý (miễn INSERT `source='reversal'`). `accounting_periods` + `accounting_period_events` chặn DELETE/TRUNCATE (events chặn cả UPDATE). Tenant chưa bật cờ: trigger không làm gì, không tạo dòng kỳ. **Down 070 giờ từ chối chạy khi 071 còn** (trigger 071 đọc `tenant_feature_flags`); test round-trip 070 chạy down 071 trước. Test `Data/AccountingPeriodMigrationTests.cs` 9/9.
- Bước 4: `Vouchers/Periods/AccountingPeriodRepository.cs` (`ListAsync` 12 tháng, thiếu dòng = open; `ClosedAsync`; `CloseAsync` + overload trong transaction của người gọi (internal, cho test); `ReopenAsync`; `ListEventsAsync`; `AccountingPeriodErrors.LockedPeriodOf(ex)`). Close: tương lai (P13) → dòng kỳ `FOR UPDATE` → đã khoá (P10) → M−1 mở + có chứng từ (P4) → còn `pending_approval`/`approved` (P5, tối đa 200 id) → UPDATE + event `period_closed`, cùng transaction. Reopen: lý do bắt buộc → `FOR UPDATE` → chưa khoá (P10) → UPDATE + cảnh báo M+1 (P6) + event `period_reopened` (lý do, `details.warnings`). Test `Data/AccountingPeriodRepositoryTests.cs` 9/9: thứ tự, chờ duyệt, lặp/tương lai, lý do + cảnh báo + event, năm 12 tháng, **song song**: ghi sổ đang chạy giữ lệnh khoá đến khi commit (rồi khoá được) / rollback (khoá bị từ chối kèm id), khoá đang chạy làm submit + PATCH song song lỗi AQ001, 12 vòng `Task.WhenAll(post, close)` không bao giờ để chứng từ `approved` trong kỳ khoá. Mutation bỏ `FOR SHARE` → 2 test đỏ (ca ngược lại vẫn được `INSERT … ON CONFLICT` chặn vì nó chờ transaction đang sửa dòng kỳ).
- Bước 5: `Endpoints/AccountingPeriodEndpoints.cs` (`MapAccountingPeriodEndpoints`, nhóm `/accounting-periods` policy `department_member`; close/reopen thêm `department_admin` + kiểm `IsInRole("admin")` + header agent → 403 `forbidden_actor`; TO/SA → 403 qua `TryScope`; cờ tắt → 409 `voucher_flow_v2_disabled`; kỳ sai → 400 `invalid_period`). 409 `period_locked` {period, message}: filter nhóm `/vouchers` (`PeriodLockedResult`) + `PeriodLockedExceptionHandler` toàn cục (`AddExceptionHandler`). `accountingDate` trên `CreateVoucherRequest`/`UpdateVoucherRequest` (tham số mặc định cuối, không vỡ chỗ gọi cũ) + cột `VoucherRecord.AccountingDate`; SQL chỉ ghi khi `voucher_flow_v2_enabled(tenant)` (tenant chưa bật cờ: bỏ qua khi tạo/trích xuất; PATCH → 400 `accounting_date_requires_v2`). `apply-invoice`/`apply-expense` kiểm kỳ **trước** khi ghi sổ chi tiết (`LockedBeforePostingAsync`), vì sổ chi tiết được ghi trong transaction riêng trước khi đổi `status` — trigger chỉ chặn được nửa sau; PR5 gộp một transaction. Test `Data/AccountingPeriodEndpointTests.cs` 13/13.
- Bước 6: `VoucherValidationContext.ClosedPeriods` (null = tenant chưa bật cờ → quy tắc không chạy); `PERIOD_LOCKED` error, field `accounting_date` nếu có, không thì `document_date`; `VoucherValidator.Version` 2 → 3. `VoucherValidationService` nhận thêm `IAccountingPeriodRepository` (5 chỗ dựng trong test đã cập nhật), chỉ đọc kỳ khoá khi bật cờ. Submit chứng từ của kỳ khoá: validator ra lỗi → chuyển `needs_fix` bị trigger chặn → 409 `period_locked`. 4 unit test + 1 kiểm tra tích hợp.
- Bước 7: agent `_conflict_text` xử lý `period_locked` (nêu kỳ, "KHÔNG thử lại, KHÔNG đổi ngày để lách", chỉ hướng ADMIN mở lại); `save_journal_suggestions` giờ cũng trả 409 qua `_conflict_text` (trước đây báo "Save journal failed" chung chung); test khẳng định agent không có tool nào về kỳ. Job trích xuất: `ReplaceExtractedAsync` gặp AQ001 → `FailExtractionAsync` mã `period_locked`, stage `save` (không ném lỗi → không vòng thử lại Hangfire), không ghi ngày; lưu bút toán gặp AQ001 → bỏ qua + log. 071 mở rộng `chk_vouchers_extraction_error_code` thêm `period_locked` (down trả lại danh sách 063, đổi dòng `period_locked` thành `agent_execution_failed`). Web: nhãn `period_locked` trong `extraction-failure-banner.tsx`. Agent 411 pass / 8 skip, ruff sạch.
