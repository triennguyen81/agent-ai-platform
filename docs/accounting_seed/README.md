# Accounting KB Seed — 8 notes mẫu

Tài liệu mẫu để bootstrap kho tri thức kế toán (TT200) cho AIQ.

## Cách dùng

1. **Review nội dung** từng file `.md` dưới đây. Sửa các con số / mức thuế cho khớp với năm hiệu lực hiện hành (NQ giảm VAT 8% có thể đã hết hạn vào thời điểm bạn đọc).
2. Vào https://app.cattuong.ai/knowledge (cần role `admin` hoặc `super_admin`).
3. Bấm nút **Bulk upload Markdown** ở panel bên phải.
4. Chọn **8 file `.md`** trong folder này (giữ Ctrl + click hoặc kéo từ Explorer).
5. Hệ thống đọc YAML frontmatter (title + tags) tự động. Bấm **Upload 8 notes**.
6. 8 note xuất hiện trong KB của bạn — sẵn sàng cho chat trả lời theo skills kế toán.

## 8 notes

| # | File | Chủ đề | Dùng cho câu hỏi |
|---|---|---|---|
| 1 | `01_he-thong-tai-khoan-tt200.md` | Hệ thống TK kế toán TT200 đầy đủ 9 loại | "TK 156 là gì?", "Liệt kê TK loại 3", "TK con của 333?" |
| 2 | `02_bieu-thue-vat-2026.md` | 4 mức VAT + cách tính + hoàn thuế | "Tách VAT 110tr đã gồm 10%", "Mặt hàng nào VAT 5%?" |
| 3 | `03_bieu-thue-tncn-2026.md` | 7 bậc lũy tiến + giảm trừ + BHXH | "Lương 30tr TNCN bao nhiêu?", "Gross net 50tr?" |
| 4 | `04_thue-tndn-2026.md` | Thuế suất + ưu đãi + CP được trừ | "TNDN của DN khu công nghệ cao?", "Lỗ chuyển kỳ sau bao lâu?" |
| 5 | `05_template-dinh-khoan-pho-bien.md` | 20 nghiệp vụ định khoản mẫu | "Mua hàng VAT 10% định khoản sao?", "Trả lương, định khoản?" |
| 6 | `06_deadline-to-khai-2026.md` | Lịch deadline cả năm + phạt chậm | "Khi nào nộp Q3/2026?", "Quyết toán TNDN 2025?" |
| 7 | `07_quy-trinh-ke-toan-cuoi-thang.md` | Checklist 10 bước chốt sổ tháng | "Quy trình cuối tháng?", "Kết chuyển 911 thế nào?" |
| 8 | `08_quy-dinh-hoa-don-dien-tu-tt78.md` | HĐĐT theo TT78 + định khoản | "Hoá đơn sai sửa sao?", "Nội dung bắt buộc trên HĐĐT?" |

## Gợi ý thêm sau khi pilot

Sau 1 tuần dùng, bổ sung những Note user thực sự cần (xem KB gap qua `/admin` chart):

- TT133 (DN vừa nhỏ) — nếu có client SME
- Hệ thống TK tài khoản ngân hàng cụ thể (vd MB, VCB) — định khoản dòng tiền
- Bảng giá dịch vụ kế toán nội bộ
- FAQ kế toán team hỏi nhau (lưu từ chat hỏi-đáp thực tế qua **"Lưu vào kiến thức"**)
- Văn bản pháp luật mới được CQT ban hành (kiểm tra tháng/quý)

## Update khi luật thay đổi

Khi có thông tư / nghị quyết mới:

1. Sửa note tương ứng (vd `02_bieu-thue-vat-2026.md` khi NQ giảm 8% hết hạn)
2. Re-upload qua bulk MD (sẽ tạo note **mới** — Note cũ vẫn còn, cần xoá thủ công nếu muốn)
3. Hoặc edit trực tiếp Note trong `/knowledge` nếu thay đổi nhỏ

## Skill liên quan

Các skill kế toán đã wire vào platform — kích hoạt tự động khi user hỏi câu liên quan:

- `accounting-vn-style` (always_on khi keyword kế toán) — format số tiền, định khoản, xưng hô
- `journal-entry-formatter` (keyword định khoản) — format 2 dòng Nợ-Có
- `tax-calculation` (keyword thuế) — show breakdown từng bậc
- `compliance-citation` (keyword văn bản / có RAG) — trích dẫn TT/NĐ/Luật

Xem [skills_authoring.md](../skills_authoring.md) nếu muốn viết skill mới.
