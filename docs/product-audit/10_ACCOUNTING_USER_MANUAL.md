# 10 — Hướng dẫn nghiệp vụ kế toán trên AIQ

Dành cho: kế toán viên (USER) và kế toán trưởng / trưởng phòng (ADMIN) của phòng ban đã bật nghiệp vụ kế toán.
Nguyên tắc: **AI đọc, trích xuất và đề xuất — người có thẩm quyền (ADMIN) quyết định và ghi sổ.** Mọi bút toán đã ghi sổ không sửa được, chỉ đảo bằng bút toán ngược có lý do.
Ảnh: [screenshots/](screenshots/) (dữ liệu demo công ty Sáu Trực).

> **Điều kiện chung:** menu **NGHIỆP VỤ** (Kế toán, Chứng từ, Hợp đồng, Khách hàng, Nhà cung cấp, Chi phí) chỉ hiện khi ADMIN đã bật nghiệp vụ tương ứng cho phòng ([09 §5](09_ADMIN_MANUAL.md)); MST công ty phải có trong hồ sơ công ty ([09 §3](09_ADMIN_MANUAL.md)).

---

## §1. Tải và xử lý chứng từ

- **Mục đích:** số hoá hoá đơn GTGT (mua/bán), phiếu thu, phiếu chi, uỷ nhiệm chi, sao kê ngân hàng, đề nghị hoàn ứng.
- **Vai trò:** USER, ADMIN.
- **Đường dẫn:** NGHIỆP VỤ → **Chứng từ** (`/vouchers`) → **Thêm chứng từ** (`/vouchers/upload`) — ảnh `admin-vouchers.png`, `admin-vouchers_upload.png`.

**Các bước**
1. Chọn **Loại chứng từ** (khuyến nghị: *Tự động nhận diện*).
2. Kéo thả file vào ô bên phải (JPG, PNG, WebP, BMP, PDF; CSV/XLSX cho sao kê; nhiều file; tối đa 25 MB/file).
3. Bấm **Chạy OCR + AI extract**. Hệ thống trả về ngay; OCR và trích xuất chạy nền, trang chi tiết tự làm mới.
4. Mở chứng từ từ danh sách. Kiểm tra:
   - **AIQ nhận diện** loại chứng từ + độ tin cậy + căn cứ; sai thì bấm **Đổi loại chứng từ**.
   - **Bên bán / Bên mua / Số tiền / Chi tiết dòng hàng**: sửa trường sai.
   - **Kiểm tra dữ liệu** (validator): MST sai định dạng/sai số kiểm tra, tổng tiền ≠ tiền hàng − chiết khấu + thuế, thuế suất không hiệu lực vào ngày hoá đơn, ngày ở tương lai, MST công ty không khớp bên mua/bán.
   - **Công nợ nhà cung cấp / khách hàng**: công nợ hiện tại, tuổi nợ, cờ rủi ro (NCC mới, quá hạn, chi tiền mặt lớn, > 500 triệu cần duyệt cấp 2).
   - **Bút toán (TT200)**: bút toán AI đề xuất.
5. Nếu kết quả trích xuất kém: **Trích xuất lại**.

**Danh sách chứng từ:** lọc theo trạng thái (Đã trích xuất, Đã xác thực, Đang OCR, Chờ AI trích, OCR lỗi, Trích xuất thất bại, Từ chối), tìm theo số HĐ / tên / MST, sắp xếp, xuất file.

**Xác thực hoá đơn với cơ quan thuế** (hoá đơn GTGT, đề nghị hoàn ứng có hoá đơn) — ảnh `admin-voucher-invoice-in.png`
1. USER hoặc trợ lý AI chỉ **đề xuất** kết quả.
2. **ADMIN** tự tra cứu trên nguồn chính thức (cổng hoadondientu.gdt.gov.vn, cổng nhà cung cấp HĐĐT, hoặc bản gốc), nhập lại thông tin từng hoá đơn (MST người bán, ký hiệu, số, ngày, tổng tiền), ghi chú bằng chứng (≥ 10 ký tự), tích xác nhận đã tự đối chiếu → xác thực.
3. Hệ thống so khớp thông tin nhập với dữ liệu chứng từ; lệch là từ chối.
4. Kết luận tiêu cực: **Đánh dấu không hợp lệ** hoặc **Không tra cứu được** (chỉ ADMIN, có lý do + nguồn).
5. Nếu sau đó sửa MST/số/ngày/tổng tiền của hoá đơn, xác thực **tự mất hiệu lực** và phải xác thực lại.

**Kết quả mong đợi:** chứng từ "Đã xác thực"; lịch sử xác thực lưu đầy đủ ai, khi nào, nguồn nào.

**Lỗi thường gặp**

| Hiện tượng | Cách xử lý |
|---|---|
| Chứng từ bị **Từ chối** ngay: "Trùng hoá đơn: <số> của MST <mst> đã tồn tại" | Hệ thống coi trùng khi cùng MST người bán và cùng số HĐ — **kể cả khác ký hiệu/năm**. Nếu đúng là hoá đơn khác, báo ADMIN (lỗi đã ghi nhận G-10) |
| Cảnh báo "thuế suất 8% không hiệu lực" cho hoá đơn trước 07/2025 | Bảng thuế suất chưa có các đợt giảm 8% trước 07/2025 (G-27) — kiểm tra tay |
| "Chưa cấu hình MST công ty" | TENANT_OWNER nhập MST trong hồ sơ công ty |

**Giới hạn hiện tại:** không đọc được file XML hoá đơn điện tử (dùng PDF); không có liên kết hoá đơn gốc ↔ hoá đơn điều chỉnh/thay thế; quy trình **nộp → duyệt / trả lại / từ chối** chứng từ chỉ có khi công ty bật quy trình chứng từ v2 (hiện tắt trên môi trường demo).

---

## §2. Bút toán

- **AI đề xuất bút toán** khi xử lý chứng từ hoặc khi bạn yêu cầu trong chat. Với phiếu thu và đề nghị hoàn ứng, hệ thống chỉ chấp nhận các cặp tài khoản trong danh mục cho phép (ví dụ Nợ 1111/1121 – Có 131; Nợ 641/642/627 – Có 141/334; Nợ 1331 – Có 141/334).
- **ADMIN quyết định** chấp nhận / từ chối từng bút toán đề xuất trên trang chứng từ.
- **Ghi sổ** xảy ra khi ADMIN ghi nhận nghiệp vụ (công nợ, thu/chi tiền, chi phí — các mục §3–§5). Bút toán đã ghi sổ hiển thị nhãn **ĐÃ GHI NHẬN (POSTED)**, không sửa được; muốn huỷ phải **đảo** (có lý do).
- Công cụ **Định khoản** (`/accounting/journal`) và **Hoá đơn → định khoản** (`/accounting/invoice`): AI gợi ý định khoản từ mô tả/văn bản hoá đơn — chỉ để tham khảo, không ghi sổ.

**Giới hạn hiện tại:** chưa có sổ cái, sổ chi tiết theo tài khoản, bảng cân đối phát sinh; chưa có màn hình đóng/mở kỳ kế toán.

---

## §3. Công nợ phải thu (khách hàng) và phải trả (nhà cung cấp)

- **Đường dẫn:** NGHIỆP VỤ → **Khách hàng** (`/customers`) / **Nhà cung cấp** (`/suppliers`) — ảnh `admin-customers_id.png`, `admin-suppliers_id.png`.
- **Xem:** danh mục, MST, hạn mức tín dụng, thời hạn thanh toán, tài khoản ngân hàng, công nợ hiện tại, tuổi nợ, hoá đơn đang mở, lần thanh toán gần nhất.
- **Sửa danh mục / tài khoản ngân hàng / ngưng hoạt động:** ADMIN.

**Ghi nhận công nợ từ hoá đơn (ADMIN):** trên trang hoá đơn bán (đầu ra) → ghi nhận phải thu; hoá đơn mua (đầu vào) → ghi nhận phải trả. Hệ thống tự xác định chiều mua/bán từ MST công ty và chặn nhầm chiều.

**Thu tiền khách hàng từ phiếu thu**
1. Tải phiếu thu (§1). Hệ thống đề xuất khớp với hoá đơn đang mở: khớp đủ, thanh toán một phần, trả thừa (phần thừa vào 3388), trả trước khi có hoá đơn.
2. USER có thể đề xuất; **ADMIN duyệt và ghi nhận**.
3. Phiếu thu ngoại tệ bị chặn (chưa có nguồn tỷ giá) — xử lý tay.

**Chiết khấu thanh toán:** ADMIN xác nhận chiết khấu theo điều khoản hợp đồng khi ghi nhận thu tiền.

**Nhắc nợ:** hệ thống quét hằng ngày, gửi thông báo khi khách hàng quá hạn.

---

## §4. Sao kê ngân hàng

- **Đường dẫn:** Chứng từ → Thêm chứng từ → tải file sao kê (PDF/CSV/XLSX) — ảnh `admin-voucher-bank-statement.png`.
- **Hệ thống làm:** tách từng giao dịch; nhận diện khách hàng / nhà cung cấp theo tài khoản ngân hàng, tên pháp lý, nội dung chuyển khoản (có độ tin cậy); đánh dấu MATCHED / NEEDS_REVIEW / UNMATCHED.
- **Bạn làm:**
  1. Kiểm tra từng giao dịch; sửa đối tác bằng **Chọn khách hàng** / **Chọn NCC**; **Nhận diện lại** khi đã bổ sung tài khoản ngân hàng đối tác.
  2. **ADMIN**: **Ghi nhận công nợ** → phân bổ vào hoá đơn → **Duyệt & ghi nhận** (tiền vào: Nợ 111/112 – Có 131; tiền ra: Nợ 331 – Có 111/112).
  3. Sai: **Điều chỉnh phân bổ** hoặc **Đảo giao dịch** (có lý do).

**Giới hạn hiện tại:** luồng tiền ra (thanh toán nhà cung cấp từ sao kê) chưa hoàn thiện như tiền vào; phí ngân hàng chưa tự hạch toán.

---

## §5. Chi phí và hoàn ứng (đề nghị thanh toán / công tác phí)

- **Đường dẫn:** NGHIỆP VỤ → **Chi phí** (`/expenses`) → **Đề nghị hoàn ứng / thanh toán công tác phí** (`/expenses/claims/new`) — ảnh `admin-expenses_claims_new.png`, `admin-voucher-expense-claim.png`.

**Các bước**
1. **Người đề nghị (USER/ADMIN):** chọn nhân viên, thêm các khoản chi kèm chứng từ (nhiều file), gửi.
2. **Hệ thống:** đọc từng chứng từ; kiểm tra chính sách chi của phòng (hạn mức theo khoản / theo đề nghị, bắt buộc chứng từ, trùng hoá đơn); tách VAT; đánh dấu khoản **không được trừ** khi tính thuế TNDN (không có hoá đơn); tính phần tạm ứng đã nhận và số còn phải trả nhân viên.
3. **ADMIN — Duyệt phòng ban:** xem kết quả "Luồng 03 — Kết quả xử lý đề nghị hoàn ứng", loại trừ / xác nhận từng khoản, cấp ngoại lệ chính sách nếu cần.
4. **ADMIN — Duyệt kế toán:** ghi nhận chi phí (Nợ 641/642/627 theo tài khoản cấu hình cho loại chi, Nợ 1331 phần VAT khấu trừ; Có 141 phần tạm ứng, Có 334 phần còn phải trả).
5. **ADMIN — Chi tiền:** chi trả nhân viên (Nợ 334 – Có 111x/112x). Nếu đề nghị có hoá đơn cần xác thực, phải xác thực hoá đơn trước (§1).

**Lưu ý phân quyền:** cùng một ADMIN được thực hiện cả 3 bước duyệt, kể cả với đề nghị của chính mình (quyết định sản phẩm). USER không duyệt được.

**Giới hạn hiện tại**
- Chỉ hỗ trợ trường hợp công ty còn phải trả nhân viên; trường hợp nhân viên phải hoàn lại tiền tạm ứng thừa chưa có.
- Phần **VAT được khấu trừ** chỉ dựa vào việc khoản chi có số hoá đơn — **chưa xét** điều kiện thanh toán không dùng tiền mặt (từ 5 triệu đồng theo luật hiện hành) và hoá đơn đứng tên công ty. Kế toán phải tự kiểm tra (G-03).
- Nhãn rủi ro "Chi TM > 30M (vi phạm TT78)" dùng ngưỡng **không đúng luật hiện hành** — bỏ qua nhãn này, áp dụng ngưỡng 5 triệu.

---

## §6. Hợp đồng

- **Đường dẫn:** NGHIỆP VỤ → **Hợp đồng** (`/contracts`) → **Thêm hợp đồng** (`/contracts/upload`) — ảnh `admin-contracts_id.png`.
- **Các bước:** tải hợp đồng (nhiều tài liệu) → AI trích xuất bên A/B, giá trị, thời hạn, điều khoản thanh toán, phạt, bảo hành → kiểm tra / sửa → xử lý → **ADMIN duyệt** và **bàn giao**.
- **Theo dõi:** checklist nghĩa vụ (nhắc trước hạn qua thông báo), lịch thanh toán và đối chiếu với tiền thu thực tế, phạt trả chậm theo kỳ, liên kết chứng từ ↔ hợp đồng.
- **Phân quyền:** USER tải và xử lý hợp đồng của mình; duyệt, bàn giao, xoá chỉ ADMIN.

---

## §7. Báo cáo

| Báo cáo | Đường dẫn | Trạng thái |
|---|---|---|
| Trạng thái chứng từ | `/admin/voucher-status` | Hoạt động |
| Công nợ khách hàng / nhà cung cấp | Trang chi tiết đối tác; hỏi trợ lý | Hoạt động |
| Báo cáo tuần cho ban giám đốc (tiền thu, công nợ, quá hạn, chi phí theo phòng) | Hỏi trợ lý có công cụ "weekly summary" | Chỉ qua chat / API, chưa có trang |
| Báo cáo tài chính (B01, B02, LCTT) | `/accounting/reports` | **Chỉ là khung do AI soạn** từ ghi chú hoặc số liệu bạn dán — **không lấy từ sổ sách**, không dùng để nộp |
| Trang "Reports" của tenant / phòng ban | menu Reports | Hiện chỉ hiển thị nhật ký hoạt động AI |

---

## §8. Thuế

> **Cảnh báo:** các chức năng thuế dưới đây **chưa đủ tin cậy để dùng cho kê khai thật**.

| Chức năng | Đường dẫn | Dùng được? |
|---|---|---|
| Tách giá – thuế GTGT một hoá đơn (0/5/8/10%) | `/accounting/tax-calc` (tab VAT) | Dùng được để tham khảo; kết quả là văn bản AI, chưa làm tròn đồng |
| Tính thuế TNCN | `/accounting/tax-calc` (tab TNCN) | **Không** — đang dùng giảm trừ 11 triệu / 4,4 triệu và biểu 7 bậc; kỳ 2026 áp dụng 15,5 triệu / 6,2 triệu và biểu 5 bậc |
| Tính thuế TNDN | `/accounting/tax-calc` (tab TNDN) | **Không** — mặc định 20%, không tự áp 15%/17% cho doanh nghiệp nhỏ |
| Hạn nộp tờ khai | `/accounting/deadline`; hỏi trợ lý | **Không** — hạn quý ghi ngày 30 (đúng là ngày cuối tháng đầu quý sau, ví dụ 31/10), hạn quyết toán TNCN cá nhân sai, chưa dời ngày nghỉ |
| Cấu hình quy tắc thuế | — | Chưa có |
| Lập tờ khai 01/GTGT, 05/QTT-TNCN, 03/TNDN; tổng hợp VAT theo kỳ | — | Chưa có |

Chi tiết sai lệch và căn cứ pháp lý: [docs/audit/TAX_WORKFLOW_AUDIT_REPORT.md](../audit/TAX_WORKFLOW_AUDIT_REPORT.md).
