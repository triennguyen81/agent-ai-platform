# Product audit — tiến độ (để tiếp tục nếu bị ngắt)

Kế hoạch: `/home/troy/.claude/plans/pasted-content-id-f460-b-n-l-joyful-whistle.md`

## Đợt 0 — Baseline (2026-11-19) ✅
- Nhánh `feat/phase-0-risk-tax` @ `b0d7aaf`; thay đổi chưa commit: luồng đăng nhập → chat (5 file web + `web/src/lib/default-agent.ts`). Phase 0 đã bỏ (code + DB 072–076).
- Container: gateway/agent/web build từ HEAD (+ web login change), hạ tầng (postgres pgvector 16, redis, qdrant, minio, caddy, prometheus, grafana, loki, promtail) chạy.
- Test an toàn: Gateway non-DB **1211/1211 pass**; Agent **411 pass / 8 skip** (cần TEST_KNOWLEDGE_DSN); Web vitest **107/107**. Gateway DB integration (thư mục `Data/`, 494 test) **SKIPPED** theo nguyên tắc không ghi DB.
- Tài khoản hoạt động (SELECT): SUPER_ADMIN `demo@aiq.local`; tenant "cty sáu trực": TENANT_OWNER `sautruc@sautruc.com`, ADMIN `admin1@sautruc.com`, **không có USER hoạt động**; tenant "Test 123", "Verification Tenant A/B", "AIQ Default Tenant" (3 USER tự đăng ký).
- DB có 1.295 schema ngoài `public` (rác test).
- Evidence: `evidence/gateway-routes.tsv` (364 route + policy), `evidence/web-pages.tsv` (83 trang + gate).

## Phát hiện tích luỹ
- F-SEC-01 `POST /auth/register` công khai → tự tạo USER trong tenant `default` (AuthEndpoints.cs:14,109-150; web `/signup` 200).
- F-ACC-01 `/approval-queue` dùng policy legacy `"admin"` (super_admin/tenant_owner/admin), tenant-wide, không scope phòng ban → TENANT_OWNER duyệt được (trái CLAUDE.md), ADMIN phòng A duyệt chứng từ phòng B. Approve chỉ đổi trạng thái (không auto-apply).
- F-DATA-01 1.295 schema test trong DB live.

## Đợt tiếp theo
1 (nền tảng & quản trị) → 2 (agent) → 3 (chứng từ) → 4 (kế toán) → 5 (thuế) → 6 (quy trình/giám sát) → 7 (bảo mật) → live (cần mật khẩu) → 8 (viết tài liệu).

## Đợt 1–7 (tĩnh) — 2026-11-19 ✅
- Viết xong: 01 (173 chức năng, đếm bằng script), 03, 04.
- Phát hiện thêm: không tenant nào bật `voucher_flow_v2` (vòng đời duyệt + khoá kỳ tắt trên demo); 7 trang usage/report/audit là bí danh của `/admin/audit`; `audit_log` Gateway không có API đọc; Agent `/metrics` 404 (Prometheus target down); `/openapi/v1.json` 500; gateway chạy `ASPNETCORE_ENVIRONMENT=Development`; agent tools không gọi route ghi sổ (có test chặn).
- Live: chờ người dùng điền `/home/troy/.aiq-audit-creds` (file mẫu đã tạo, chmod 600).

## Live — 2026-11-19 ✅
- Đăng nhập 4 vai trò OK; 92 lượt mở trang, 90 ảnh (`screenshots/`), 15 lượt chat (`evidence/chat-probes.json`), 13 phép thử API GET (`evidence/api-isolation-probes.json`). Không có ghi dữ liệu nghiệp vụ (SELECT xác nhận).
- 01 cập nhật: 174 chức năng — PASS 74, PARTIAL 33, MISSING 11, BROKEN 7, UNVERIFIED 49 (IT 21, không test 28).
- Viết xong: 02, 05, 06. Phát hiện mới: DLP tắt + chat bỏ qua DLP; AgentInternalNetworks chưa cấu hình; cổng DB/Prometheus/Grafana mở 0.0.0.0; agent "Onboarding Smoke" là agent mặc định của USER Test 123; câu gợi ý chat nha khoa; hồ sơ công ty Test 123 tên "Sáu Trực" (dữ liệu test).
- Còn lại: 07, 08, 09, 10, 11, 12, 13, 14, 15, 00, quality gate.

## Đợt 8 — Tài liệu + quality gate — 2026-11-19 ✅
- Đủ 16 tài liệu 00–15; 92 ảnh; evidence: gateway-routes.tsv, web-pages.tsv, ui-walk-results.json, chat-probes.json, api-isolation-probes.json.
- Quality gate: số liệu 01/02/00 khớp (174 · PASS 74 · PARTIAL 33 · MISSING 11 · BROKEN 7 · UNVERIFIED 49); mọi ảnh/liên kết tồn tại; không có mật khẩu/token trong tài liệu (chuỗi khớp duy nhất nằm trong MST mẫu); git chỉ thêm tài liệu.
- Khôi phục 7 báo cáo thuế `docs/audit/TAX_*.md, VAT/PIT/CIT_AUDIT_REPORT.md` (bị xoá khi rewind 2026-11-18) từ `stash@{1}^3` bằng `git show` — stash không bị thay đổi.
- HOÀN THÀNH.
