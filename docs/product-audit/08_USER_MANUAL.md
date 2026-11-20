# 08 — Hướng dẫn sử dụng AIQ (người dùng doanh nghiệp)

Dành cho: nhân viên (USER) và trưởng bộ phận (ADMIN) dùng AIQ hằng ngày.
Hướng dẫn quản trị: [09_ADMIN_MANUAL.md](09_ADMIN_MANUAL.md) · Nghiệp vụ kế toán: [10_ACCOUNTING_USER_MANUAL.md](10_ACCOUNTING_USER_MANUAL.md).
Phiên bản sản phẩm: bản chạy ngày 2026-11-19. Ảnh minh hoạ trong thư mục [screenshots/](screenshots/) (dữ liệu demo).

> Tài liệu chỉ mô tả chức năng đã kiểm tra hoạt động. Mỗi mục có phần **Giới hạn hiện tại** — vui lòng đọc trước khi dùng cho công việc thật.

---

## §1. AIQ là gì (Platform Overview)

AIQ là nền tảng trợ lý AI cho doanh nghiệp, được chia theo **công ty (tenant) → phòng ban → trợ lý AI (agent)**.

| Bạn có thể | Ở đâu |
|---|---|
| Hỏi đáp với trợ lý AI của phòng ban, tra cứu chứng từ, công nợ, hợp đồng | Chat |
| Tải chứng từ (hoá đơn, phiếu thu/chi, uỷ nhiệm chi, sao kê, đề nghị hoàn ứng) để AI đọc và trích xuất | Chứng từ |
| Tải và theo dõi hợp đồng | Hợp đồng |
| Xem khách hàng, nhà cung cấp, công nợ | Khách hàng / Nhà cung cấp |
| Lập đề nghị hoàn ứng / thanh toán công tác phí | Chi phí |
| Nhận thông báo (nợ quá hạn, hạn hợp đồng…) | Biểu tượng chuông |

**Nguyên tắc an toàn:** trợ lý AI chỉ đọc, trích xuất và **đề xuất**. Mọi quyết định ghi sổ, duyệt, chi tiền do người có thẩm quyền (ADMIN phòng ban) thực hiện trên hệ thống. Trợ lý sẽ từ chối nếu được yêu cầu tự ghi sổ hay chi tiền.

**Vai trò:**

| Vai trò | Dùng để |
|---|---|
| USER | Dùng trợ lý được gán, tải và nộp chứng từ, lập đề nghị thanh toán |
| ADMIN (trưởng phòng / kế toán trưởng) | Như USER + duyệt, ghi sổ, quản lý trợ lý và tri thức của phòng |
| TENANT_OWNER | Quản trị công ty: phòng ban, người dùng, hồ sơ công ty (không làm nghiệp vụ kế toán) |
| SUPER_ADMIN | Quản trị nền tảng (không xem dữ liệu nghiệp vụ của công ty) |

---

## §2. Đăng nhập, hồ sơ, đăng xuất

- **Mục đích:** truy cập AIQ an toàn.
- **Điều kiện:** tài khoản do quản trị công ty tạo.
- **Vai trò:** tất cả.

**Các bước**
1. Mở địa chỉ AIQ của công ty, nhập **Email** và **Mật khẩu**, bấm **Đăng nhập**.
2. Nếu hệ thống yêu cầu đổi mật khẩu lần đầu, làm theo hướng dẫn trên màn hình.
3. Sau khi đăng nhập:
   - USER / ADMIN vào **thẳng màn hình Chat** với trợ lý dùng lần trước (lần đầu: trợ lý đầu tiên theo tên).
   - TENANT_OWNER / SUPER_ADMIN vào **Tổng quan (Dashboard)**.
4. Xem hồ sơ / đổi mật khẩu: bấm vào **email của bạn** ở góc dưới menu trái (mở `/profile`, "Hồ sơ / Bảo mật").
5. Đăng xuất: biểu tượng ⇥ cạnh tên ở góc dưới bên trái.

**Lỗi thường gặp**

| Hiện tượng | Cách xử lý |
|---|---|
| "Sai email hoặc mật khẩu" | Kiểm tra lại; sau 10 lần sai/phút hệ thống tạm khoá đăng nhập 1 phút |
| Vào trang "Không có quyền truy cập" | Trang đó không dành cho vai trò của bạn — dùng menu trái |
| Sau đăng nhập vào "Trợ lý AI của tôi" thay vì Chat | Bạn chưa được gán trợ lý nào đang hoạt động — liên hệ ADMIN phòng |

**Giới hạn hiện tại:** đăng nhập SSO (Clerk) chưa được cấu hình trên môi trường demo.

---

## §3. Trợ lý AI của tôi

- **Mục đích:** xem các trợ lý bạn được dùng và chọn trợ lý làm việc.
- **Điều kiện:** ADMIN đã gán trợ lý cho bạn (USER) hoặc trợ lý thuộc phòng của bạn (ADMIN).
- **Đường dẫn:** menu trái → **Trợ lý AI** (`/my-agents`); hoặc mục **TRỢ LÝ CỦA BẠN** trên menu trái.

**Các bước**
1. Mở **Trợ lý AI** để xem danh sách trợ lý, trạng thái (chỉ trợ lý *production* và không bị tắt mới dùng được).
2. Bấm **Dùng agent này →** trên thẻ trợ lý để mở hội thoại, hoặc bấm tên trợ lý ở mục **TRỢ LÝ CỦA BẠN** để đổi trợ lý đang dùng (dấu ✓ là trợ lý hiện tại).

**Kết quả:** dòng "Đang dùng: <tên trợ lý>" ở đầu menu trái đổi theo.
**Ảnh:** `admin-my-agents.png`

---

## §4. Chat với trợ lý AI

- **Mục đích:** hỏi đáp nghiệp vụ, tra cứu dữ liệu của phòng (chứng từ, công nợ, hợp đồng) bằng ngôn ngữ tự nhiên.
- **Điều kiện:** có ít nhất một trợ lý đang hoạt động.
- **Vai trò:** USER, ADMIN.
- **Đường dẫn:** **New chat** trên menu trái (`/chat`).

**Các bước**
1. Gõ câu hỏi vào ô dưới cùng, bấm **Send** (hoặc dùng micro để nói).
2. Trợ lý trả lời; nếu cần, trợ lý tự tra dữ liệu bằng công cụ được cấp (ví dụ danh sách chứng từ, công nợ khách hàng).
3. Lịch sử hội thoại nằm ở cột **LỊCH SỬ CHAT**; bấm **Mới** để bắt đầu cuộc trò chuyện khác.

**Ví dụ câu hỏi đã kiểm tra hoạt động** (trợ lý "Trợ lý kế toán" của phòng có công cụ dữ liệu):
- "Phòng mình hiện có bao nhiêu chứng từ đã xác thực? Liệt kê 5 chứng từ gần nhất."
- "Công nợ phải thu hiện tại của Công ty TNHH Thương mại Minh An là bao nhiêu? Có khoản nào quá hạn không?"
- "Hoá đơn 1C26TDP 0001041 có những thông tin gì?"

**Kết quả mong đợi:** câu trả lời có số liệu lấy từ hệ thống; khi không có dữ liệu, trợ lý nói rõ là không có thay vì đoán.
**Ảnh:** `admin-root.png`

**Lỗi thường gặp**

| Hiện tượng | Nguyên nhân / xử lý |
|---|---|
| Trợ lý trả lời "không có công cụ truy cập dữ liệu khách hàng/công nợ" | Trợ lý được gán cho bạn chưa được cấp công cụ dữ liệu — báo ADMIN phòng cấu hình trợ lý |
| Trợ lý chỉ trả lời một câu cố định (ví dụ `ONBOARDING_SMOKE_OK`) | Bạn đang dùng trợ lý thử nghiệm — chọn trợ lý khác ở mục **TRỢ LÝ CỦA BẠN** |
| Trả lời chậm (10–30 giây) | Trợ lý đang tra nhiều dữ liệu; chờ hoặc hỏi hẹp hơn |
| Lỗi "hết hạn mức phiên" | Hạn mức mô hình AI tạm hết; thử lại sau thời điểm được báo |

**Giới hạn hiện tại — đọc kỹ**
- **Không dùng kết quả tính thuế TNCN, TNDN và hạn nộp tờ khai từ trợ lý** cho công việc thật: các con số này hiện sai so với quy định kỳ 2026 (xem [10 §8](10_ACCOUNTING_USER_MANUAL.md)).
- Kiến thức pháp luật của trợ lý có thể lỗi thời (ví dụ ngưỡng thanh toán không dùng tiền mặt). Luôn đối chiếu văn bản hiện hành.
- 4 câu hỏi gợi ý trên màn hình chat hiện là nội dung mẫu không liên quan (nha khoa) — bỏ qua.
- Thông tin bạn gõ vào chat được gửi tới mô hình AI **không qua bộ che dữ liệu cá nhân** — không dán số CCCD, tài khoản ngân hàng cá nhân… vào chat.

---

## §5. Tri thức (Knowledge) dùng trong chat

- **Mục đích:** trợ lý trả lời dựa trên tài liệu nội bộ của phòng.
- **Điều kiện:** ADMIN phòng đã tạo kho tri thức và **gắn** vào trợ lý ([09 §5](09_ADMIN_MANUAL.md)).
- **Vai trò:** USER, ADMIN dùng qua chat; chỉ ADMIN quản lý.

**Cách dùng:** đặt câu hỏi liên quan nội dung tài liệu; trợ lý chỉ được đọc những kho tri thức gắn với nó.

**Giới hạn hiện tại:** chưa kiểm tra được trên môi trường demo (trợ lý demo chưa gắn kho tri thức). Nếu trợ lý trả "không đủ căn cứ trong tri thức được cấp", nghĩa là tài liệu chưa được gắn hoặc không chứa thông tin đó.

---

## §6. Thông báo

- **Đường dẫn:** biểu tượng chuông góc trên phải (`/notifications`).
- **Nội dung:** nợ khách hàng quá hạn, hạn nghĩa vụ hợp đồng, phạt trả chậm, kết quả khớp phiếu thu, đề nghị thanh toán cần xử lý.
- **Các bước:** bấm chuông → xem danh sách → bấm vào thông báo để mở chứng từ/hợp đồng liên quan.
- **Ảnh:** `admin-notifications.png`
- **Giới hạn hiện tại:** chỉ có thông báo trong ứng dụng; chưa gửi email/Zalo/SMS.

---

## §17. Xử lý sự cố (Troubleshooting)

| Hiện tượng | Nguyên nhân thường gặp | Cách xử lý |
|---|---|---|
| Không thấy menu Kế toán / Chứng từ / Hợp đồng / Khách hàng / Nhà cung cấp / Chi phí | Phòng ban chưa bật nghiệp vụ (capability) tương ứng | ADMIN phòng bật ở **Department → Nghiệp vụ** |
| Trang "Không có quyền truy cập" | Trang không dành cho vai trò của bạn | Dùng menu trái theo vai trò |
| Chứng từ tải lên bị **Từ chối** ngay với thông báo "Trùng hoá đơn" | Đã có chứng từ cùng MST người bán và cùng số hoá đơn (kể cả năm khác) | Kiểm tra chứng từ cũ; nếu là hoá đơn khác năm, báo ADMIN (giới hạn đã biết) |
| Chứng từ đứng ở "Đang OCR" lâu | Hàng đợi OCR xử lý tuần tự hoặc hạn mức AI tạm hết | Chờ; ADMIN có thể bấm **Trích xuất lại** |
| "Trích xuất thất bại" | Ảnh mờ, file không đúng loại, hoặc hạn mức AI | Chụp lại rõ hơn (PDF có lớp chữ là tốt nhất), tải lại |
| Không nộp/duyệt được chứng từ, báo `voucher_flow_v2_disabled` | Quy trình duyệt chứng từ v2 chưa bật cho công ty | Quản trị bật cờ quy trình cho công ty |
| Trợ lý trả lời không liên quan | Đang dùng nhầm trợ lý | Đổi trợ lý ở mục **TRỢ LÝ CỦA BẠN** |
