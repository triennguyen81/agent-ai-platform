# 01 — Requirement Inventory (Xuly-ctkt.pdf)

**Nguồn:** `docs/Xuly-ctkt.pdf` — 7 trang (Google Slides xuất PDF, 720×405pt, tác giả "Truyen Nguyen").
Đã đọc cả lớp text (`pdftotext -layout`, từng trang) và bản render hình ảnh của cả 7 trang;
nội dung hình khớp 100% với text, **không có** ảnh nhúng, bảng, sơ đồ hay phụ lục.

**Baseline audit:** commit `9622f8b` + worktree chưa commit (184 file thay đổi) — ngày 2026-10-08.

Quy ước phân loại nội dung:

- **[PDF]** — yêu cầu có trong PDF (trích nguyên văn).
- **[SUY LUẬN]** — suy luận cần người dùng xác nhận.
- **[ĐỀ XUẤT]** — đề xuất bổ sung, KHÔNG phải requirement bắt buộc.

## Nội dung PDF (nguyên văn, theo trang)

| Trang | Tiêu đề | Nội dung |
|---|---|---|
| 1 | Xử lý chứng từ kế toán | Trang bìa |
| 2 | Phạm vi chứng từ sử lý | 1. Hợp đồng — Cần xác định kế toán xử lý các thông tin gì trong hợp đồng. 2. Phiếu thu (công nợ) — Xử lý 2 loại phiếu thu: a. Thông báo/sao kê ngân hàng; b. Phiếu thu của phòng tài vụ/kế toán (thu bên ngoài hệ thống ngân hàng. Nhân viên thu tiền viết phiếu thu khi nhập tiền vào công ty). 3. Thanh toán các hóa đơn — Nhân viên khai báo các hoá đơn chứng từ cần thanh toán |
| 3 | Cấu hình Agent | Agent được cấu hình trước khi thực hiện giao dịch. Việc cấu hình này có thể cấu hình lại nếu có thay đổi. Cấu hình được thực hiện bởi người quản lý Agent. 1. Loại chứng từ — scope hiện tại là 3 loại được định nghĩa trên. 2. Định dạng cho từng loại chứng từ. 3. Quy trình thực hiện của mỗi loại chứng từ (workflow). 4. Thông tin cần xử lý cho mỗi loại chứng từ (có thể gom vào #3) |
| 4 | Quy trình xử lý chứng từ | 1. Nhận chứng từ: upload hoặc đọc từ folder thông qua connector. 2. Validate chứng từ — kiểm tra chứng từ có hợp lệ trong scope chứng từ đã định nghĩa. Việc validate dựa trên định dạng chứng từ đã được cấu hình ⇒ xác định được loại chứng từ. 3. Rút trích thông tin chứng từ (có thể gom cùng bước #2). 4. Xử lý chứng từ theo quy trình của chứng từ đã được cấu hình theo loại chứng từ đã xác định. 5. Thông báo kết quả xử lý |
| 5 | Quy trình xử lý HD | 1. Cần tham khảo quy trình xử lý HD của kế toán. 2. Cách tham khảo: a. Tham khảo với chuyên gia kế toán; b. Đề xuất từ claude |
| 6 | Quy trình xử lý phiếu thu công nợ | 1. Lấy mã Khách hàng/Hợp đồng dựa vào thông tin từ phiếu thu: (1) STK của khách hàng; (2) tên khách hàng (3) thông tin thanh toán đơn hàng — Cần tham khảo sao kê/thông báo của ngân hàng ⇒ mã khách hàng, số tiền thu. 2. Lấy công nợ của khách hàng ⇒ tổng công nợ. 3. Lấy thông tin chiết khấu thanh toán của khách hàng từ HD (validate — time và amount). 4. Xử lý công nợ: a. Số tiền thu = tổng công nợ − chiết khấu ⇒ đề xuất bút toán; b. Số tiền thu ≠ tổng công nợ − chiết khấu ⇒ nếu chưa đủ ⇒ cập nhật công nợ; nếu thừa ⇒ xử lý thừa. 5. Đề xuất bút toán cho kế toán viên review và xác nhận. 6. Thông báo kết quả xử lý cho các bên liên quan; thông báo email hay messenger tuỳ vào kênh mà khách hàng đã đăng ký |
| 7 | Xử lý hoá đơn thanh toán — Expenses | 1. Đọc khai báo thanh toán từ hệ thống: (1) thông tin thanh toán; (2) chứng từ ⇒ đối soát thông tin và chứng từ. 2. Kiểm tra tính hợp lệ của chứng từ và các thông tin thanh toán xem có đúng quy trình của công ty hay không (kỳ công tác, yêu cầu mua hàng, thanh toán). 3. Đề xuất thanh toán cho kế toán viên review và xác nhận. 4. Thông báo cho các thành phần liên quan |

> PDF **không** quy định định khoản Nợ/Có nào, không quy định trạng thái nghiệp vụ, mẫu chứng từ,
> vai trò duyệt (ngoài "kế toán viên", "người quản lý Agent", "nhân viên"), kênh connector,
> hay chính sách chiết khấu/xử lý thừa. Mọi nội dung đó trong các requirement dưới đây đều
> được đánh dấu [SUY LUẬN] hoặc "không quy định".

## Danh mục requirement (28 requirement [PDF])

Cột "Phụ thuộc" chỉ các requirement khác cần có trước.

### A. Phạm vi chứng từ (trang 2)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái nghiệp vụ | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-SC-01 | Hợp đồng trong phạm vi | Hợp đồng là loại chứng từ cần xử lý; "cần xác định kế toán xử lý các thông tin gì trong hợp đồng" | Tr.2 mục 1 | Kế toán [SUY LUẬN] | File hợp đồng | Thông tin kế toán của HĐ | **Không quy định** thông tin nào | — | Không quy định | Hợp đồng | Không quy định | Không quy định | XL-HD-01 |
| XL-SC-02 | Phiếu thu loại a — Thông báo/sao kê ngân hàng | Xử lý thông báo/sao kê ngân hàng như một loại phiếu thu công nợ | Tr.2 mục 2a | Kế toán | Sao kê/thông báo NH | Khoản thu được nhận diện | Không quy định định dạng | XL-CF-02 | Không quy định | Sao kê, thông báo NH | Không quy định | Không quy định | XL-AR-01..08 |
| XL-SC-03 | Phiếu thu loại b — Phiếu thu phòng tài vụ | Phiếu thu tiền ngoài hệ thống ngân hàng do nhân viên thu tiền viết khi nhập tiền vào công ty | Tr.2 mục 2b | Nhân viên thu tiền (lập), kế toán (xử lý) | Phiếu thu | Khoản thu được xử lý | Thu ngoài NH | XL-CF-02 | Không quy định | Phiếu thu | Không quy định | Không quy định | XL-AR-01..08 |
| XL-SC-04 | Thanh toán hoá đơn (Expenses) | Nhân viên khai báo các hoá đơn chứng từ cần thanh toán | Tr.2 mục 3 | Nhân viên (khai báo) | Hoá đơn/chứng từ | Khai báo thanh toán | Không quy định | XL-CF-02 | Không quy định | Hoá đơn, chứng từ chi | Không quy định | Không quy định | XL-EX-01..04 |

### B. Cấu hình Agent (trang 3)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-CF-01 | Cấu hình Agent trước giao dịch | Agent được cấu hình trước khi giao dịch; cấu hình lại được khi có thay đổi; do **người quản lý Agent** thực hiện | Tr.3 đoạn mở đầu | Người quản lý Agent | Cấu hình | Agent đã cấu hình | Cấu hình trước khi xử lý | — | Không quy định (vòng đời cấu hình) | — | — | Không quy định | — |
| XL-CF-02 | Cấu hình loại chứng từ | Cấu hình loại chứng từ; scope hiện tại = 3 loại (Hợp đồng, Phiếu thu, Thanh toán hoá đơn) | Tr.3 mục 1 | Người quản lý Agent | Danh sách loại | Loại được bật | Scope 3 loại | XL-CF-01 | — | 3 loại ở tr.2 | — | — | XL-PR-03 |
| XL-CF-03 | Cấu hình định dạng từng loại | Định dạng cho từng loại chứng từ | Tr.3 mục 2 | Người quản lý Agent | Định dạng | Định dạng đã cấu hình | **Không định nghĩa "định dạng"** (mẫu/layout hay trường dữ liệu?) [SUY LUẬN] | XL-CF-02 | — | — | — | — | XL-PR-03 |
| XL-CF-04 | Cấu hình quy trình (workflow) từng loại | Quy trình thực hiện của mỗi loại chứng từ | Tr.3 mục 3 | Người quản lý Agent | Workflow | Workflow theo loại | Không quy định bước | XL-CF-02 | — | — | — | — | XL-PR-05 |
| XL-CF-05 | Cấu hình thông tin cần xử lý | Thông tin cần xử lý cho mỗi loại (có thể gom vào #3) | Tr.3 mục 4 | Người quản lý Agent | Danh sách trường | Schema trích xuất | — | XL-CF-02 | — | — | — | — | XL-PR-04 |

### C. Quy trình xử lý chứng từ chung (trang 4)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-PR-01 | Nhận chứng từ — upload | Nhận chứng từ bằng upload | Tr.4 mục 1 | Người dùng | File | Chứng từ trong hệ thống | — | — | Không quy định | 3 loại | — | File lỗi: không quy định | — |
| XL-PR-02 | Nhận chứng từ — folder connector | Đọc chứng từ từ folder thông qua connector | Tr.4 mục 1 | Hệ thống | Folder | Chứng từ trong hệ thống | Không quy định loại folder/connector | — | — | 3 loại | — | Không quy định | — |
| XL-PR-03 | Validate & xác định loại | Kiểm tra chứng từ hợp lệ trong scope đã định nghĩa; validate dựa trên định dạng đã cấu hình ⇒ xác định loại chứng từ | Tr.4 mục 2 | Agent/hệ thống | Chứng từ | Loại chứng từ, hợp lệ/không | Ngoài scope ⇒ không hợp lệ [SUY LUẬN] | XL-CF-02, XL-CF-03 | — | 3 loại | — | Ngoài scope: không quy định xử lý | XL-PR-04 |
| XL-PR-04 | Rút trích thông tin | Rút trích thông tin chứng từ (có thể gộp bước 2) | Tr.4 mục 3 | Agent | Chứng từ | Dữ liệu cấu trúc | Theo thông tin cấu hình (XL-CF-05) | XL-PR-03 | — | 3 loại | — | Không quy định | XL-PR-05 |
| XL-PR-05 | Xử lý theo workflow cấu hình | Xử lý theo quy trình đã cấu hình cho loại đã xác định | Tr.4 mục 4 | Agent/kế toán | Dữ liệu + workflow | Kết quả xử lý | — | XL-CF-04 | — | 3 loại | — | — | XL-AR-*, XL-EX-*, XL-HD-01 |
| XL-PR-06 | Thông báo kết quả xử lý | Thông báo kết quả xử lý | Tr.4 mục 5 | Hệ thống | Kết quả | Thông báo | Không quy định người nhận/kênh | XL-PR-05 | — | — | — | — | — |

### D. Quy trình xử lý Hợp đồng (trang 5)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-HD-01 | Quy trình xử lý HĐ | "Cần tham khảo quy trình xử lý HD của kế toán" — qua chuyên gia kế toán hoặc đề xuất từ Claude | Tr.5 mục 1–2 | Kế toán / chuyên gia | — | Quy trình HĐ | **PDF chưa định nghĩa quy trình** | — | — | Hợp đồng | Không quy định | — | XL-SC-01 |

### E. Quy trình xử lý phiếu thu công nợ (trang 6)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-AR-01 | Xác định khách hàng / hợp đồng | Lấy mã KH/HĐ từ thông tin phiếu thu: (1) STK khách hàng, (2) tên KH, (3) thông tin thanh toán đơn hàng; tham khảo sao kê/thông báo NH ⇒ mã KH, số tiền thu | Tr.6 mục 1 | Agent/hệ thống | Phiếu thu / dòng sao kê | Mã KH (và HĐ), số tiền | 3 nguồn nhận diện | XL-SC-02/03 | — | Phiếu thu, sao kê | — | Không xác định được: không quy định | XL-AR-02 |
| XL-AR-02 | Lấy tổng công nợ KH | Từ mã KH ⇒ tổng công nợ | Tr.6 mục 2 | Hệ thống | Mã KH | Tổng công nợ | — | XL-AR-01; công nợ phải tồn tại [SUY LUẬN: nguồn tạo công nợ không quy định] | — | — | — | — | XL-AR-04 |
| XL-AR-03 | Chiết khấu thanh toán từ HĐ | Lấy chiết khấu thanh toán của KH từ HĐ; validate theo thời gian và số tiền | Tr.6 mục 3 | Hệ thống | HĐ, phiếu thu | Số chiết khấu hợp lệ | Validate time & amount (chi tiết không quy định) | XL-AR-01, XL-SC-01 | — | Hợp đồng | Không quy định TK chiết khấu | Không quy định | XL-AR-04 |
| XL-AR-04 | Thu đủ (= công nợ − chiết khấu) | Số tiền thu = tổng công nợ − chiết khấu ⇒ đề xuất bút toán | Tr.6 mục 4a | Agent/hệ thống | Số thu, công nợ, CK | Đề xuất bút toán | So sánh trên **tổng công nợ** | XL-AR-02/03 | — | — | Không quy định | — | XL-AR-07 |
| XL-AR-05 | Thu thiếu ⇒ cập nhật công nợ | Số thu < (công nợ − CK) ⇒ cập nhật công nợ | Tr.6 mục 4b | Hệ thống | Số thu | Công nợ còn lại | — | XL-AR-02 | — | — | Không quy định | — | XL-AR-07 |
| XL-AR-06 | Thu thừa ⇒ xử lý thừa | Số thu > (công nợ − CK) ⇒ "xử lý thừa" | Tr.6 mục 4b | Không quy định | Số thu | Không quy định | **Không định nghĩa cách xử lý thừa** | XL-AR-02 | — | — | Không quy định | — | — |
| XL-AR-07 | Đề xuất bút toán cho kế toán viên review & xác nhận | Đề xuất bút toán; kế toán viên review và xác nhận | Tr.6 mục 5 | Agent đề xuất; kế toán viên xác nhận | Kết quả xử lý công nợ | Bút toán đã xác nhận | Có bước xác nhận của con người | XL-AR-04/05/06 | Đề xuất → xác nhận [SUY LUẬN] | — | Không quy định | — | XL-AR-08 |
| XL-AR-08 | Thông báo theo kênh KH đăng ký | Thông báo kết quả cho các bên liên quan; email hoặc messenger theo kênh KH đã đăng ký | Tr.6 mục 6 | Hệ thống | Kết quả | Email/messenger | Kênh theo đăng ký của KH | XL-AR-07 | — | — | — | Không quy định | — |

### F. Xử lý hoá đơn thanh toán — Expenses (trang 7)

| ID | Chức năng | Nội dung yêu cầu | Căn cứ | Actor | Input | Output | Business rules | Tiền đề | Trạng thái | Chứng từ | Định khoản | Ngoại lệ | Phụ thuộc |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XL-EX-01 | Đọc khai báo & đối soát | Đọc khai báo thanh toán từ hệ thống: (1) thông tin thanh toán; (2) chứng từ ⇒ đối soát thông tin và chứng từ | Tr.7 mục 1 | Agent/hệ thống | Khai báo + chứng từ | Kết quả đối soát | Đối soát thông tin khai báo ↔ chứng từ | XL-SC-04 | — | Hoá đơn, chứng từ | — | Lệch: không quy định | XL-EX-02 |
| XL-EX-02 | Kiểm tra hợp lệ theo quy trình công ty | Kiểm tra chứng từ và thông tin thanh toán có đúng quy trình công ty (kỳ công tác, yêu cầu mua hàng, thanh toán) | Tr.7 mục 2 | Agent/hệ thống | Khai báo, chứng từ, dữ liệu quy trình công ty | Hợp lệ/không | Quy trình công ty **không được mô tả** | XL-EX-01 | — | — | — | — | XL-EX-03 |
| XL-EX-03 | Đề xuất thanh toán cho kế toán viên review & xác nhận | Đề xuất thanh toán; kế toán viên review và xác nhận | Tr.7 mục 3 | Agent đề xuất; kế toán viên xác nhận | Kết quả kiểm tra | Thanh toán đã xác nhận | Có xác nhận của con người | XL-EX-02 | — | — | Không quy định | — | XL-EX-04 |
| XL-EX-04 | Thông báo các thành phần liên quan | Thông báo cho các thành phần liên quan | Tr.7 mục 4 | Hệ thống | Kết quả | Thông báo | Không quy định kênh/người nhận | XL-EX-03 | — | — | — | — | — |

## Suy luận cần xác nhận [SUY LUẬN]

| ID | Nội dung | Lý do |
|---|---|---|
| INF-01 | Công nợ phải thu (XL-AR-02) được tạo từ việc ghi nhận **hoá đơn bán**; hợp đồng tự nó không tạo công nợ | PDF dùng "tổng công nợ" nhưng không nêu nguồn tạo công nợ; hoá đơn bán không nằm trong scope 3 loại |
| INF-02 | "Định dạng" (XL-CF-03) có thể là danh sách trường dữ liệu/schema hoặc mẫu bố cục (template) | PDF không định nghĩa |
| INF-03 | "Kế toán viên review và xác nhận" là bước con người bắt buộc trước khi ghi sổ / chi tiền | Tr.6 mục 5, tr.7 mục 3 |
| INF-04 | "Tổng công nợ − chiết khấu" so sánh trên toàn bộ công nợ KH hay theo từng hoá đơn | PDF dùng "tổng công nợ" |
| INF-05 | "Thanh toán các hoá đơn" (tr.2 mục 3) là đề nghị thanh toán/hoàn ứng của nhân viên (Expenses), không phải hoá đơn mua của NCC | Tr.7 tiêu đề "Expenses" + "khai báo thanh toán" |
| INF-06 | Tạm ứng (kỳ công tác) là một phần của "quy trình công ty" cần kiểm tra ở XL-EX-02 | Tr.7 mục 2 nêu kỳ công tác |

## Đề xuất bổ sung (KHÔNG tính vào thống kê) [ĐỀ XUẤT]

- Sổ cái / bảng cân đối số phát sinh từ bút toán đã ghi sổ để đối chiếu số dư 131/141/331/334.
- Quy trình tạm ứng nhân viên (đề nghị → duyệt → chi Nợ 141) để XL-EX-02 có dữ liệu kỳ công tác.
- Đảo/huỷ ghi nhận đề nghị thanh toán.
- Khoá dữ liệu chứng từ sau khi đã ghi sổ.
