---
title: Quy trình kế toán cuối tháng — checklist 10 bước
tags:
  - quy-trình
  - cuối-tháng
  - chốt-sổ
  - checklist
---

# Quy trình kế toán cuối tháng — checklist 10 bước

Áp dụng cho DN dùng TT200, chốt sổ hằng tháng.

## Bước 1 — Đối chiếu công nợ

- [ ] In sổ chi tiết TK 131 (phải thu KH) — đối chiếu với từng KH có dư cuối kỳ
- [ ] In sổ chi tiết TK 331 (phải trả NCC) — đối chiếu với từng NCC
- [ ] Gửi biên bản đối chiếu (đặc biệt cho KH/NCC có dư trên 100 triệu)
- [ ] Xử lý chênh lệch: ghi tăng/giảm vào TK 138/338 (chờ xử lý)

## Bước 2 — Kiểm kê quỹ + ngân hàng

- [ ] Kiểm kê tiền mặt thực tế tại quỹ → đối chiếu sổ TK 111
- [ ] Lấy sổ phụ NH cho tất cả TK 112 → đối chiếu sổ kế toán
- [ ] Xử lý các khoản tiền đang chuyển: phát sinh trong tháng nhưng chưa về (TK 113)

## Bước 3 — Kiểm kê hàng tồn kho

- [ ] Báo cáo nhập-xuất-tồn từng kho → đối chiếu sổ chi tiết TK 156/152/155
- [ ] Xử lý chênh lệch:
  - Thiếu hàng có lý do (hao hụt định mức) → vào CP (TK 632/642)
  - Thiếu hàng chưa rõ → TK 1381 (chờ xử lý)
  - Thừa hàng → TK 3381 (chờ xử lý)

## Bước 4 — Tính lương, BHXH, TNCN

- [ ] Tính lương cho tất cả NLĐ (bao gồm phụ cấp, thưởng nếu có)
- [ ] Tính BHXH, BHYT, BHTN — phần DN và phần NLĐ
- [ ] Tính TNCN từng NLĐ theo bậc lũy tiến
- [ ] In bảng lương → ký nhận
- [ ] Định khoản:
  - Nợ 622/627/641/642 / Có 334 (lương DN trả)
  - Nợ 622/627/641/642 / Có 338 (BHXH DN trả)
  - Nợ 334 / Có 338 (BH NLĐ trả — trừ vào lương)
  - Nợ 334 / Có 3335 (TNCN)

## Bước 5 — Trích khấu hao TSCĐ

- [ ] Mở sổ TK 211, 213 → kiểm tra TSCĐ đã đăng ký khấu hao
- [ ] Tính khấu hao tháng cho từng TSCĐ (đường thẳng / số dư giảm dần)
- [ ] Phân bổ vào CP từng bộ phận:
  - SX → TK 627
  - Bán hàng → TK 641
  - QLDN → TK 642
- [ ] Định khoản: Nợ 627/641/642 / Có 214

## Bước 6 — Phân bổ công cụ dụng cụ, CP trả trước

- [ ] Tính số phân bổ CP trả trước (TK 142/242) tháng này
- [ ] Tính số phân bổ CCDC (TK 153) đã xuất dùng
- [ ] Định khoản: Nợ 627/641/642 / Có 142/242/153

## Bước 7 — Trích trước chi phí phải trả (nếu có)

- [ ] Ước tính các CP đã phát sinh nhưng chưa có hoá đơn (vd lương cuối kỳ, lãi vay, BH trả trước)
- [ ] Định khoản: Nợ 627/641/642 / Có 335

## Bước 8 — Kết chuyển doanh thu, chi phí → 911

```
Bước 8a — Kết chuyển doanh thu:
Nợ TK 511, 515, 711 / Có TK 911

Bước 8b — Kết chuyển chi phí thời kỳ:
Nợ TK 911 / Có TK 632, 635, 641, 642, 811, 821
```

## Bước 9 — Tính thuế TNDN tạm tính + xác định kết quả

- [ ] Tính LN trước thuế = Tổng PS Có TK 911 − Tổng PS Nợ TK 911
- [ ] Tính thuế TNDN tạm = LN × thuế suất (20% mặc định)
- [ ] Định khoản: Nợ 8211 / Có 3334
- [ ] Kết chuyển CP thuế: Nợ 911 / Có 8211
- [ ] Số dư TK 911 (Có > Nợ → lãi; Nợ > Có → lỗ) → kết chuyển:
  - Lãi: Nợ 911 / Có 4212
  - Lỗ: Nợ 4212 / Có 911

## Bước 10 — In sổ + lưu trữ

- [ ] Sổ Nhật ký chung (sổ tổng hợp)
- [ ] Sổ Cái tất cả TK có phát sinh
- [ ] Sổ chi tiết: 131, 331, 156, 152, 211, 334, 333, 3331, 421
- [ ] Bảng cân đối số phát sinh tháng
- [ ] Bảng cân đối kế toán tháng
- [ ] Đóng quyển + lưu file điện tử có chữ ký số

## Lưu ý

- Khi có **sai sót phát hiện kỳ sau**, sửa bằng bút toán điều chỉnh, không sửa trực tiếp số liệu cũ
- **Số dư đầu kỳ tháng sau** = số dư cuối kỳ tháng trước → kiểm tra khớp 100%
- **Đối chiếu CB-TK + CĐKT** trước khi đóng sổ: tổng tài sản = tổng nguồn vốn

## Khi nào nên chốt sổ?

- DN nhỏ: cuối **tuần đầu** tháng sau
- DN vừa: chậm nhất ngày **10** tháng sau
- DN lớn: chậm nhất ngày **15** tháng sau (cùng với tờ khai GTGT T trước)

Càng chốt sớm càng dễ phát hiện sai sót + có thời gian sửa trước deadline tờ khai.

## Tham chiếu

- TT200/2014/TT-BTC Chương III — Sổ kế toán
- TT53/2016/TT-BTC sửa đổi TT200
- Luật Kế toán 88/2015/QH13
