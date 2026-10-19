# 06 — Gap Analysis

Chỉ liệt kê requirement **PARTIAL / MISSING / INCORRECT / PDF UNCLEAR** (trạng thái lấy từ `02`) và các rủi ro toàn vẹn (`04`, `05`).

Mức ảnh hưởng theo quy tắc audit: CRITICAL khi có nguy cơ sai số dư, ghi nhận trùng, sai tenant, chi tiền không duyệt, mất truy vết.

## A. Chức năng còn thiếu (MISSING)

| ID | Thiếu gì | Mức | Bằng chứng | Phụ thuộc quyết định nghiệp vụ |
|---|---|---|---|---|
| XL-PR-02 | Nhận chứng từ từ folder qua connector (folder / email / drive) | MEDIUM | Không có ingest connector trong `gateway/src`, `agent/src` | Loại nguồn (thư mục chia sẻ, email, Google Drive…), tần suất, xử lý trùng |
| XL-AR-08 | Thông báo kết quả cho KH qua email / messenger theo kênh KH đăng ký | MEDIUM | `notifications` chỉ in-app (`Endpoints/NotificationEndpoints.cs:15-18`); `sim_customers` không có kênh đăng ký | Kênh, nội dung, thời điểm, có duyệt nội dung không |

## B. Triển khai một phần (PARTIAL)

| ID | Đã có | Còn thiếu | Mức |
|---|---|---|---|
| XL-SC-02 | Sao kê CSV / XLSX / trích xuất, khớp KH, ghi sổ | Loại chứng từ "thông báo ngân hàng / giấy báo có" đơn lẻ | MEDIUM |
| XL-CF-03 | Cấu hình **trường** (tên, kiểu, bắt buộc) theo loại | "Định dạng" dùng để nhận diện loại (mẫu / bố cục) — PDF chưa định nghĩa | MEDIUM |
| XL-CF-04 / XL-PR-05 | Workflow: prompt / knowledge / tool / require_review; cổng duyệt bền vững | Quy trình nghiệp vụ theo loại do code cố định (handler), không cấu hình được | MEDIUM |
| XL-PR-03 | Phân loại xác định + LLM + chặn loại ngoài cấu hình | Xác định loại dựa trên "định dạng đã cấu hình" | MEDIUM |
| XL-PR-06 | Banner lỗi trích xuất; in-app cho một số sự kiện | Thông báo khi xử lý xong / lỗi cho người nộp | MEDIUM |
| XL-AR-01 | Nhận diện KH (STK, MST, số HĐ, mã KH, tên) | Nhận diện **hợp đồng** trực tiếp từ phiếu thu (hiện chỉ qua hoá đơn có liên kết; số HĐ trích xuất chỉ hiển thị — `ICashSettlementRepository.cs:413`) | MEDIUM |
| XL-AR-03 | Điều khoản CK có cấu trúc; tính xác định; đề xuất; xác nhận tính lại | Ghi sổ CK (thiếu TK được phê duyệt); CK cho thanh toán một phần; CK theo "tổng công nợ" | HIGH |
| XL-AR-04 | Thu đủ khi không có CK ⇒ đề xuất & ghi sổ | Trường hợp có CK không ghi sổ được | HIGH |
| XL-EX-01 | Đọc từng chứng từ, tổng do server tính | Đối soát "thông tin khai báo" ↔ chứng từ khi hai nguồn khác nhau (đề nghị 1 chứng từ chỉ có cờ `receipt_attached`) | MEDIUM |
| XL-EX-02 | Định mức, trùng, thuế, ngoài kỳ công tác | Yêu cầu mua hàng, lệnh công tác chính thức, tạm ứng theo chuyến | HIGH |
| XL-EX-04 | In-app khi trả về / từ chối / chờ ngoại lệ | Thông báo khi duyệt / ghi nhận / chi; email | LOW |

## C. Triển khai sai (INCORRECT)

Không có requirement PDF nào bị triển khai **trái** yêu cầu. Lưu ý một điểm lệch diễn giải, chưa đủ căn cứ để coi là sai:

- PDF tr.6 mục 4 so sánh số thu với "**tổng công nợ** − chiết khấu".
- Hệ thống đánh giá chiết khấu và phân bổ **theo từng hoá đơn**.
- Đây là điểm cần xác nhận (D-03), không xếp INCORRECT.

## D. PDF chưa rõ (PDF UNCLEAR)

| ID | Câu hỏi cần trả lời | Hiện trạng hệ thống (tham khảo) |
|---|---|---|
| XL-SC-01 | Kế toán cần lấy thông tin gì từ hợp đồng? | Đang lấy: các bên, giá trị, VAT, lịch thanh toán, mốc, phạt, giữ lại, bảo hành, điều khoản CK |
| XL-HD-01 | Quy trình xử lý HĐ của kế toán là gì? | Đang có: xử lý → cảnh báo → duyệt → bàn giao; lịch TT liên kết hoá đơn |
| XL-AR-06 | "Xử lý thừa" là gì (giữ trả trước 131, 3388, hoàn tiền, cấn trừ đợt sau, ngưỡng)? | Để "chưa phân bổ" trên Có 131; ADMIN phân bổ sau |

## E. Rủi ro toàn vẹn ngoài danh mục PDF (từ `04`, `05`)

| ID | Rủi ro | Mức | Ghi chú |
|---|---|---|---|
| R-01 / AG-01 | Chứng từ đã ghi sổ (không phải đề nghị thanh toán) có thể bị trích xuất lại / `apply-extraction` (kể cả bởi tool Agent) ghi đè dữ liệu và trạng thái ⇒ mất truy vết | **HIGH** | Ứng viên CRITICAL nếu kế toán dùng chứng từ trên hệ thống làm chứng từ gốc pháp lý |
| R-02 | Hoá đơn không sinh bút toán ⇒ không có sổ cái / cân đối | **HIGH** | Ảnh hưởng toàn bộ giai đoạn Reporting / Reconciliation |
| R-04 | Không có nghiệp vụ chi tạm ứng (141); tạm ứng nhập bằng seed; cấn trừ toàn bộ | **HIGH** | Liên quan XL-EX-02 (kỳ công tác) |
| R-03 | `current_ar` không trừ tiền KH đã trả chưa phân bổ | MEDIUM | |
| R-05 | Không đảo được ghi nhận / chi đề nghị thanh toán | MEDIUM | |
| AG-02 | OCR Agent không chịu được gọi đồng thời (đã giảm thiểu ở Gateway) | MEDIUM | |
| AG-04 | Báo cáo tài chính là bản Agent soạn, không từ sổ sách | MEDIUM | |
| F7-dir | `contracts.direction` dựa trên `tenants.company_tax_id` (đang null ở hầu hết tenant) khác nguồn MST của hoá đơn | LOW | |

**Không phát hiện:**
- Ghi nhận trùng.
- Sai tenant hoặc phòng ban: test cô lập đều PASS.
- Chi tiền không qua duyệt: mọi thao tác chi đều dùng `department_admin`.

Do đó không có phát hiện mức **CRITICAL** đã được xác minh.
