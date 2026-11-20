# 13 — Checklist dữ liệu & chuẩn bị demo

Môi trường: tenant **"cty sáu trực"** (Default Department) trên `localhost:3030` hoặc môi trường demo tương đương. Mọi dữ liệu là dữ liệu mẫu.

## §1. Tài khoản demo

| Vai trò | Tài khoản | Trạng thái 2026-11-19 | Việc cần làm |
|---|---|---|---|
| SUPER_ADMIN | `demo@aiq.local` | ✅ đăng nhập được | Không dùng trong demo khách hàng (tuỳ chọn) |
| TENANT_OWNER | `sautruc@sautruc.com` | ✅ | — |
| ADMIN (kế toán trưởng) | `admin1@sautruc.com` | ✅ vào thẳng chat "Trợ lý kế toán" | — |
| USER (kế toán viên) | **chưa có** USER hoạt động trong tenant Sáu Trực (`livetest.user` đang bị khoá) | ❌ | Tạo / mở khoá 1 USER, gán agent "Trợ lý kế toán" (Nhân sự), đăng nhập thử |

Mật khẩu: cấp riêng cho người trình diễn, **không** ghi vào slide hay tài liệu.

## §2. Bộ dữ liệu demo

### Có sẵn (đã kiểm tra 2026-11-19)
| Dữ liệu | Định danh | Dùng ở phần |
|---|---|---|
| Hoá đơn mua đã xác thực | 1C26TDP · 0001041 (Đại Phát, 49.500.000 ₫) | 7, 9, dự phòng 5–6 |
| Đề nghị hoàn ứng đã xử lý | DNHU-2026-1017 (3.520.000 ₫) | dự phòng 10 |
| Sao kê ngân hàng 2 giao dịch đã khớp | `06_Sao_ke_ngan_hang.pdf` | 8 |
| Khách hàng | Công ty TNHH Thương mại Minh An (MST 0200000005) | 4, 8 |
| Nhà cung cấp | Công ty TNHH Cung ứng Đại Phát (MST 0200000001) | 8 |
| Hợp đồng | "Hợp đồng dịch vụ bảo trì Q4/2026 – Đại Phát (DỮ LIỆU MẪU)" | tuỳ chọn |
| 50+ chứng từ đủ loại, nhiều trạng thái | danh sách Chứng từ | 11 |

### Phải chuẩn bị mới
| Dữ liệu | Yêu cầu | Lý do |
|---|---|---|
| 1 hoá đơn GTGT mua vào **số mới** + 2 bản dự phòng | Người bán Đại Phát (MST 0200000001), người mua Sáu Trực (MST 0123456789), ký hiệu 1C26TDP, **số chưa có trong DB** (đã dùng ở tenant Sáu Trực ngày 2026-11-19: 0000501, 0000901, 0000902, 0000991, 0000992, 0001026, 0001041, 0001091 — kiểm tra lại bằng tìm kiếm trước demo), PDF có lớp chữ | Hệ thống tự từ chối hoá đơn trùng `MST người bán + số` — **không dùng lại** `demo-output/customer-samples/02_Hoa_Don_GTGT_Mua_Vao.pdf` (số 0001026 đã có) |
| 1 đề nghị hoàn ứng ở trạng thái **chờ duyệt phòng ban** | Nhân viên demo, 2 khoản chi có hoá đơn (≤ hạn mức chính sách), hoá đơn được ADMIN xác thực trước | Phần 10 |
| (tuỳ chọn) 1 hoá đơn có lỗi cố ý | Tổng tiền ≠ tiền hàng + thuế, hoặc MST sai số kiểm tra | Minh hoạ validator ở phần 7 |

Mẫu HTML để tạo chứng từ: `docs/demo-templates/*.html` (chỉnh số, MST, ngày rồi in PDF bằng `render-pngs.sh` hoặc trình duyệt). Lưu ý các mẫu 01–08 trong thư mục này mang dữ liệu **nha khoa** (Bệnh viện RHM, Kavo) — chỉ dùng làm khung.

## §3. Checklist trước demo (T-2 ngày → T-0)

### Dữ liệu & cấu hình (T-2)
- [ ] Tạo USER demo trong Sáu Trực, gán "Trợ lý kế toán"
- [ ] Đổi mật khẩu mạnh, riêng cho từng tài khoản demo (hiện dùng chung mật khẩu yếu — SEC-13)
- [ ] **Tắt** (Disable) agent thử nghiệm "Onboarding Smoke", "zzz" hoặc thu hồi khỏi người dùng demo (G-22)
- [ ] Đổi tên / ẩn dữ liệu `LIVE-TEST…` hiển thị trong danh sách khách hàng, nhân viên, chứng từ (G-28)
- [ ] Sửa 4 câu hỏi gợi ý nha khoa trên màn hình chat, hoặc chuẩn bị lời dẫn bỏ qua (G-30)
- [ ] Quyết định có bật quy trình chứng từ v2 (`voucher_flow_v2`) cho tenant demo không; nếu bật, tổng duyệt nộp → duyệt / trả lại / từ chối (G-13)
- [ ] Hồ sơ công ty Sáu Trực: tên pháp lý, MST 0123456789, địa chỉ đúng
- [ ] Rà slide `docs/slides/aiq-intro.*`: không hứa tính năng ở [11 §4](11_DEMO_PLAN.md)

### Kỹ thuật (T-1)
- [ ] `GET /healthz/ready` → `{"status":"ready","postgres":"ok"}`
- [ ] `docker inspect aiq-agent` RestartCount không tăng sau 3 lần OCR liên tiếp
- [ ] Hạn mức mô hình AI đủ cho ~30 lượt gọi trong buổi demo (kiểm tra không có "session limit")
- [ ] Chạy integration test cho chi phí/hoàn ứng và chứng từ trên DB thử (`ExpenseClaim*`, `VoucherLifecycle*`, `TaxVerificationIntegrationTests`)
- [ ] **Tổng duyệt toàn bộ kịch bản A** trên đúng môi trường, bấm giờ; ghi lại số chứng từ đã dùng
- [ ] Chụp lại bộ ảnh dự phòng sau tổng duyệt

### Ngày demo (T-0, trước 30 phút)
- [ ] Đăng nhập sẵn TENANT_OWNER, ADMIN, USER ở 3 cửa sổ ẩn danh riêng
- [ ] Chạy 1 câu chat "khởi động" (C01) — xác nhận thời gian trả lời < 30 giây
- [ ] File hoá đơn mới + 2 bản dự phòng trên màn hình nền
- [ ] Mở sẵn tab ảnh dự phòng
- [ ] Tắt thông báo hệ điều hành; đặt trình duyệt 100% zoom, 1440×900 trở lên

## §4. Checklist sau demo

- [ ] Ghi lại câu hỏi / yêu cầu của khách hàng, đối chiếu với [15_RECOMMENDED_ROADMAP.md](15_RECOMMENDED_ROADMAP.md)
- [ ] Ghi lại mọi sự cố (thời điểm, màn hình, thông báo) → tạo việc sửa
- [ ] Xuất nhật ký Agent của buổi demo (DEPARTMENT → Audit) làm bằng chứng
- [ ] Liệt kê dữ liệu đã tạo trong buổi demo (chứng từ, đề nghị, bút toán) và quyết định giữ / dọn
- [ ] Thu hồi / đổi mật khẩu tài khoản demo nếu đã chia sẻ
- [ ] Không gửi khách ảnh hay tài liệu có tính năng PARTIAL/UNVERIFIED mà không ghi chú giới hạn
