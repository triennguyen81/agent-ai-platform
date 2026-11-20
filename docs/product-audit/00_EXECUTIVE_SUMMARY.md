# 00 — Tóm tắt điều hành: Full Product Audit AIQ

**Ngày:** 2026-11-19 · **Phiên bản:** nhánh `feat/phase-0-risk-tax` @ `b0d7aaf` + thay đổi đăng nhập → chat (chưa commit) · **Môi trường kiểm tra:** bản đang chạy local (`localhost:3030` / `:8080`).
**Phương pháp:** truy vết mã nguồn (364 route, 83 trang), chạy 1.729 test an toàn, probe engine thuế, kiểm tra live chỉ đọc bằng 4 vai trò (92 lượt mở trang, 15 lượt chat, 13 phép thử API). Không sửa mã, không ghi DB, không migration, không commit.

## 1. Kết luận

AIQ có **nền tảng kỹ thuật và kiểm soát tốt**:
- phân quyền theo phòng ban chặt, cô lập dữ liệu giữa các công ty đã kiểm chứng;
- trợ lý AI chạy theo cấu hình do máy chủ ký và **không có khả năng tự ghi sổ, duyệt hay chi tiền** (kiểm chứng cả bằng test lẫn tấn công prompt injection live);
- bút toán đã ghi sổ bất biến;
- xác thực hoá đơn bắt buộc có bằng chứng của kế toán trưởng.

Lõi nghiệp vụ **chứng từ → công nợ → thu/chi tiền → chi phí & hoàn ứng → hợp đồng** hoạt động.

Điểm yếu tập trung ở **thuế** (tính TNCN, TNDN, hạn nộp sai luật kỳ 2026; điều kiện khấu trừ VAT sai), **lớp tổng hợp** (không sổ cái, báo cáo tài chính do AI soạn, dashboard chỉ là liên kết, 7 trang báo cáo/usage/audit trỏ về cùng một trang), và **cấu hình triển khai** (DLP tắt, đăng ký công khai, cổng hạ tầng mở, chế độ Development).

| Mục tiêu | Kết luận |
|---|---|
| **Demo** | **Sẵn sàng có điều kiện** — sau 10 việc P0 (chủ yếu dữ liệu và cấu hình) và tổng duyệt; không trình diễn thuế, BCTC, dashboard số liệu |
| **Nghiệm thu** | **Chưa** — cần P1 (thuế đúng luật, quyền duyệt, dò trùng, báo cáo, audit) |
| **Production** | **Chưa** — cần P1 + P2 |

## 2. Báo cáo cuối (theo Phần 7)

| # | Hạng mục | Kết quả |
|---|---|---|
| 1 | Tổng số module | **36** (M01–M36) |
| 2 | Tổng số chức năng | **174** |
| 3 | PASS | **74** |
| 4 | PARTIAL | **33** |
| 5 | MISSING | **11** |
| 6 | BROKEN | **7** |
| 7 | UNVERIFIED | **49** (21 có integration test chưa chạy · 28 không có test tự động) |
| 8 | Critical / High | **3 / 12** (Medium 14, Low 7 — [07](07_PRODUCT_GAP_ANALYSIS.md)) |
| 9 | Có thể demo | Xem §3 |
| 10 | Chưa thể demo | Xem §4 |
| 11 | Roadmap | P0 10 việc · P1 13 · P2 11 · P3 6 — [15](15_RECOMMENDED_ROADMAP.md) |
| 12 | Tài liệu đã tạo | Xem §6 |

## 3. Chức năng có thể demo

Đã xác minh live:
- Đăng nhập & phân quyền 4 vai trò; ADMIN/USER vào thẳng chat.
- Quản lý agent phòng ban (xem cấu hình, version, kill switch).
- Chat với "Trợ lý kế toán": tra chứng từ, công nợ có số liệu thật; từ chối bịa nguồn; từ chối ghi sổ/chi tiền; từ chối dữ liệu công ty khác.
- Danh sách và chi tiết chứng từ, nhận diện loại, validator, công nợ đối tác, cờ rủi ro.
- Sao kê ngân hàng tự nhận diện đối tác.
- Khách hàng / nhà cung cấp / công nợ.
- Hợp đồng; đề nghị hoàn ứng.
- Báo cáo trạng thái chứng từ; thông báo; nhật ký hoạt động AI.

Cần tổng duyệt trước khi đưa vào demo (có mã nguồn + test, chưa chạy live thao tác ghi):
- tải chứng từ mới → OCR → trích xuất;
- duyệt và chi tiền đề nghị hoàn ứng;
- xác thực hoá đơn;
- publish agent;
- nộp/duyệt chứng từ v2 (cần bật cờ).

## 4. Chức năng chưa thể demo

| Nhóm | Chức năng | Trạng thái |
|---|---|---|
| Thuế | Tính TNCN, TNDN; hạn nộp tờ khai | BROKEN |
| Thuế | Cấu hình quy tắc thuế; tờ khai 01/GTGT, 05/QTT, 03/TNDN; VAT theo kỳ; dữ liệu lương | MISSING |
| Kế toán | Sổ cái / CĐPS; báo cáo tài chính từ sổ | MISSING / PARTIAL |
| Báo cáo | Dashboard số liệu; trang Reports, Usage | PARTIAL / BROKEN |
| Bảo mật | DLP che dữ liệu cá nhân | PARTIAL (đang tắt) |
| Audit | Tra cứu nhật ký thao tác người dùng (`audit_log`) | PARTIAL (không có UI) |
| Tri thức | RAG trên tài liệu nội bộ | UNVERIFIED (agent demo chưa gắn kho tri thức) |
| Tích hợp | MISA/ERP, XML HĐĐT, email/Zalo | MISSING |

## 5. Năm rủi ro cần lãnh đạo biết

1. **Thuế sai luật kỳ 2026** (G-01, G-02, G-09): người dùng hỏi trợ lý sẽ nhận số TNCN/TNDN và hạn nộp sai — xác nhận live.
2. **Khấu trừ VAT sai điều kiện** (G-03): ngưỡng 30 triệu thay vì 5 triệu; VAT "được khấu trừ" chỉ vì có số hoá đơn.
3. **Dữ liệu cá nhân gửi tới AI không được che** (G-06, G-07), trong khi giao diện nói ngược lại.
4. **Quyền duyệt legacy sai phạm vi** (G-04): chủ doanh nghiệp duyệt được chứng từ, trái nguyên tắc sản phẩm.
5. **Cấu hình triển khai chưa an toàn cho Internet** (G-05, G-08, G-16): đăng ký công khai, CSDL và giám sát mở cổng, chế độ Development.

## 6. Bộ tài liệu

| Tài liệu | Nội dung |
|---|---|
| [01_SYSTEM_FUNCTIONAL_INVENTORY.md](01_SYSTEM_FUNCTIONAL_INVENTORY.md) | 174 chức năng × vai trò, UI, API, backend, test, trạng thái |
| [02_FUNCTIONAL_AUDIT_REPORT.md](02_FUNCTIONAL_AUDIT_REPORT.md) | Phương pháp, kết quả tổng, BROKEN, kết quả live |
| [03_ROLE_PERMISSION_MATRIX.md](03_ROLE_PERMISSION_MATRIX.md) | 4 vai trò, 364 route theo policy, ma trận chức năng, tax governance |
| [04_ACCOUNTING_WORKFLOW_AUDIT.md](04_ACCOUNTING_WORKFLOW_AUDIT.md) | Luồng kế toán, accounting safety, business logic |
| [05_AI_AGENT_AUDIT.md](05_AI_AGENT_AUDIT.md) | Manifest, tool, isolation, RAG, hallucination, 15 lượt chat |
| [06_SECURITY_AND_DATA_ISOLATION.md](06_SECURITY_AND_DATA_ISOLATION.md) | Cô lập tenant, RBAC, SSRF, upload, secret, audit, DLP |
| [07_PRODUCT_GAP_ANALYSIS.md](07_PRODUCT_GAP_ANALYSIS.md) | 36 khoảng trống đã khử trùng, theo mức độ |
| [08_USER_MANUAL.md](08_USER_MANUAL.md) | Hướng dẫn người dùng (tổng quan, đăng nhập, trợ lý, chat, tri thức, thông báo, sự cố) |
| [09_ADMIN_MANUAL.md](09_ADMIN_MANUAL.md) | Hướng dẫn SUPER_ADMIN, TENANT_OWNER, người dùng, agent, phê duyệt, dashboard, audit |
| [10_ACCOUNTING_USER_MANUAL.md](10_ACCOUNTING_USER_MANUAL.md) | Chứng từ, bút toán, công nợ, sao kê, chi phí, hợp đồng, báo cáo, thuế |
| [11_DEMO_PLAN.md](11_DEMO_PLAN.md) | Kế hoạch 40 phút, mức sẵn sàng từng phần, rủi ro, dự phòng |
| [12_DEMO_SCRIPT.md](12_DEMO_SCRIPT.md) | Lời thoại; kịch bản thành công và khi AI lỗi; hỏi đáp |
| [13_DEMO_DATA_CHECKLIST.md](13_DEMO_DATA_CHECKLIST.md) | Tài khoản, dữ liệu, checklist trước/sau demo |
| [14_PRODUCT_READINESS.md](14_PRODUCT_READINESS.md) | 8 trục sẵn sàng, blockers, demo ≠ production |
| [15_RECOMMENDED_ROADMAP.md](15_RECOMMENDED_ROADMAP.md) | P0–P3 |
| [evidence/](evidence/), [screenshots/](screenshots/) | Bảng route, trang, kết quả live, chat, phép thử API; 92 ảnh màn hình |
