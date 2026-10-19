# 08 — Implementation Roadmap

Sắp xếp theo **phụ thuộc** và **rủi ro**. Mỗi mục ghi rõ requirement / rủi ro liên quan và quyết định nghiệp vụ cần có trước.

Không hạng mục nào được triển khai trong lần audit này.

## Giai đoạn 0 — Quyết định nghiệp vụ (chặn các giai đoạn sau)

| ID | Quyết định | Mở khoá |
|---|---|---|
| D-01 | Thông tin kế toán cần lấy từ hợp đồng; quy trình xử lý HĐ (XL-SC-01, XL-HD-01) | G3 |
| D-02 | TK hạch toán chiết khấu thanh toán; chiết khấu cho thanh toán một phần; đảo chiết khấu | G2-3 |
| D-03 | So sánh theo "tổng công nợ" hay theo từng hoá đơn (tr.6 mục 4) | G2-3 |
| D-04 | "Xử lý thừa": giữ trả trước 131 / 3388 / hoàn tiền / cấn trừ; ngưỡng tự động | G2-4 |
| D-05 | Kênh thông báo (email / Zalo / Messenger…), người nhận, nội dung, duyệt nội dung | G4 |
| D-06 | Quy trình công ty cho chi phí: lệnh công tác, yêu cầu mua hàng, tạm ứng (ai lập, ai duyệt, lưu ở đâu) | G3 |
| D-07 | Nguồn connector (thư mục, email, drive), tần suất | G4 |
| D-08 | "Định dạng chứng từ" là schema trường hay mẫu bố cục | G4 |
| D-09 | Có yêu cầu sổ cái / cân đối số phát sinh trong phạm vi sản phẩm không | G2 |

## Giai đoạn 1 — Toàn vẹn truy vết (rủi ro cao, không phụ thuộc quyết định)

| Hạng mục | Liên quan | Mức |
|---|---|---|
| Khoá dữ liệu chứng từ sau khi đã ghi sổ: chặn trích xuất lại / `apply-extraction` / ghi đè trạng thái khi chứng từ có bút toán tiền đã ghi (`cash_transactions` posted), dòng công nợ (`sim_ar_ledger` / `sim_ap_ledger`) hoặc sao kê có dòng đã ghi. Ưu tiên ở DB (trigger như `059`) và ở endpoint. Kèm test hồi quy. | R-01, AG-01 | HIGH |
| Test bắt buộc cho các đường trên (tool Agent `save_voucher` với token người tạo) | R-01 | HIGH |

## Giai đoạn 2 — Kế toán nền

| Hạng mục | Liên quan | Phụ thuộc |
|---|---|---|
| 2-1. Bút toán ghi sổ cho hoá đơn bán / mua (Nợ 131 / Có 511, 3331; Nợ 152/156/6xx, 1331 / Có 331) bằng rule tin cậy, bất biến như hiện tại | R-02 | Xác nhận TK doanh thu / chi phí theo loại hàng |
| 2-2. Sổ cái / cân đối số phát sinh / số dư theo TK từ bút toán ghi sổ + đối chiếu với `current_ar` / `current_ap` / ngân hàng | R-02, R-03 | 2-1, D-09 |
| 2-3. Ghi sổ chiết khấu thanh toán (mở khoá điểm dừng hiện có) | XL-AR-03/04 | D-02, D-03 |
| 2-4. Xử lý thu thừa theo chính sách | XL-AR-06 | D-04 |
| 2-5. Hiển thị `current_ar` có trừ tiền chưa phân bổ hoặc tách rõ "trả trước" | R-03 | 2-2 |

## Giai đoạn 3 — Hoàn thiện nghiệp vụ theo PDF

| Hạng mục | Liên quan | Phụ thuộc |
|---|---|---|
| 3-1. Nghiệp vụ tạm ứng: đề nghị → duyệt → chi (Nợ 141) → sổ chi tiết 141 theo NV / chuyến → cấn trừ theo chuyến → hoàn lại tạm ứng thừa | XL-EX-02, R-04 | D-06 |
| 3-2. Lệnh công tác / yêu cầu mua hàng làm căn cứ đối chiếu đề nghị thanh toán | XL-EX-02 | D-06 |
| 3-3. Đối soát số khai báo ↔ từng chứng từ (khi khai báo có số riêng) | XL-EX-01 | D-06 |
| 3-4. Nhận diện hợp đồng trực tiếp từ phiếu thu (số HĐ trích xuất → HĐ đã duyệt của đúng KH) | XL-AR-01 | — |
| 3-5. Loại chứng từ "thông báo / giấy báo có" ngân hàng | XL-SC-02 | — |
| 3-6. Quy trình xử lý HĐ theo kết luận của chuyên gia | XL-SC-01, XL-HD-01 | D-01 |
| 3-7. Đảo ghi nhận / chi đề nghị thanh toán | R-05 | Quyết định kế toán về bút toán đảo |

## Giai đoạn 4 — Tích hợp & thông báo

| Hạng mục | Liên quan | Phụ thuộc |
|---|---|---|
| 4-1. Thông báo kết quả xử lý (in-app → email / messenger theo kênh đăng ký của KH) | XL-PR-06, XL-AR-08, XL-EX-04 | D-05 |
| 4-2. Connector folder / email ingest (idempotent theo hash file) | XL-PR-02 | D-07 |
| 4-3. "Định dạng" cấu hình dùng để nhận diện loại; workflow theo loại cấu hình được nhiều hơn cổng duyệt | XL-CF-03/04, XL-PR-03/05 | D-08 |

## Giai đoạn 5 — Vận hành & chất lượng

| Hạng mục | Liên quan |
|---|---|
| Sửa OCR Agent an toàn khi gọi đồng thời (bỏ được tuần tự hoá ở Gateway) | AG-02 |
| E2E tự động trên trình duyệt trong CI; test đối chiếu số dư | `07` |
| Thống nhất nguồn MST công ty cho chiều hợp đồng | F7-dir |
| Ghi rõ trên `/accounting/reports` rằng đây là mẫu Agent soạn, hoặc thay bằng báo cáo từ sổ sách (sau 2-2) | AG-04 |
