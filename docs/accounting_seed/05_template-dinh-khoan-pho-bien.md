---
title: Template định khoản 20 nghiệp vụ phổ biến (TT200)
tags:
  - định-khoản
  - hạch-toán
  - template
  - cheatsheet
  - tt200
---

# 20 nghiệp vụ định khoản phổ biến — TT200

Bảng tra cứu nhanh khi cần định khoản 1 nghiệp vụ. Tất cả ví dụ giả định công ty áp dụng phương pháp khấu trừ VAT.

## I. Mua hàng / nguyên vật liệu

### 1. Mua hàng nhập kho, chưa thanh toán

```
Nợ TK 156  — Hàng hoá           | 100.000.000
Nợ TK 133  — VAT đầu vào 10%    |  10.000.000
Có TK 331  — Phải trả NB         | 110.000.000
```

### 2. Mua nguyên vật liệu trực tiếp cho sản xuất

```
Nợ TK 152  — Nguyên vật liệu    | 50.000.000
Nợ TK 133                       |  5.000.000
Có TK 331                        | 55.000.000
```

### 3. Mua công cụ dụng cụ (phân bổ nhiều kỳ)

```
Nợ TK 153  — Công cụ dụng cụ    | 20.000.000
Nợ TK 133                       |  2.000.000
Có TK 111/112                    | 22.000.000

(Khi xuất dùng — phân bổ vào CP từng tháng)
Nợ TK 627/641/642 (tuỳ bộ phận) | 2.000.000
Có TK 153                        | 2.000.000
```

### 4. Mua TSCĐ có VAT khấu trừ

```
Nợ TK 211  — TSCĐ hữu hình      | 500.000.000
Nợ TK 1332 — VAT TSCĐ           |  50.000.000
Có TK 331/341/111/112            | 550.000.000
```

## II. Bán hàng

### 5. Bán hàng cho khách, chưa thu tiền

```
Bút toán 1 — Ghi doanh thu:
Nợ TK 131  — Phải thu KH        | 220.000.000
Có TK 511  — Doanh thu          | 200.000.000
Có TK 3331 — VAT đầu ra         |  20.000.000

Bút toán 2 — Ghi giá vốn:
Nợ TK 632  — Giá vốn hàng bán   | 150.000.000
Có TK 156  — Hàng hoá           | 150.000.000
```

### 6. Bán hàng thu tiền ngay

```
Bút toán 1:
Nợ TK 111/112                    | 220.000.000
Có TK 511                        | 200.000.000
Có TK 3331                       |  20.000.000

Bút toán 2 — Giá vốn như trên
```

### 7. Khách trả lại hàng đã bán

```
Bút toán 1 — Giảm doanh thu:
Nợ TK 5212 — Hàng bán bị trả lại | 50.000.000
Nợ TK 3331                       |  5.000.000
Có TK 131                        | 55.000.000

Bút toán 2 — Nhập lại kho:
Nợ TK 156                        | 30.000.000
Có TK 632                        | 30.000.000
```

### 8. Chiết khấu thương mại cho khách (do mua nhiều)

```
Nợ TK 5211 — Chiết khấu TM       | 10.000.000
Nợ TK 3331                       |  1.000.000
Có TK 131                        | 11.000.000
```

### 9. Chiết khấu thanh toán cho khách (trả sớm)

```
Nợ TK 635  — Chi phí tài chính   | 2.000.000
Có TK 131                        | 2.000.000
```

## III. Tiền tệ

### 10. Khách hàng thanh toán nợ qua NH

```
Nợ TK 112  — Tiền gửi NH        | 220.000.000
Có TK 131                        | 220.000.000
```

### 11. Trả NCC qua NH

```
Nợ TK 331                        | 110.000.000
Có TK 112                        | 110.000.000
```

### 12. Rút tiền NH về quỹ

```
Nợ TK 111                        | 50.000.000
Có TK 112                        | 50.000.000
```

## IV. Lương + BH

### 13. Tính lương phải trả tháng

```
Nợ TK 622/627/641/642 (theo BP) | 100.000.000
Có TK 334  — Phải trả NLĐ        | 100.000.000
```

### 14. Trích BHXH, BHYT, BHTN

```
DN trả (21,5%):
Nợ TK 622/627/641/642           | 21.500.000
Có TK 3383 — BHXH                | 17.000.000
Có TK 3384 — BHYT                |  3.000.000
Có TK 3389 — BHTN                |  1.000.000
Có TK 3382 — KPCĐ                |    500.000

NLĐ trả (10,5%) — trừ vào lương:
Nợ TK 334                        | 10.500.000
Có TK 3383                       |  8.000.000
Có TK 3384                       |  1.500.000
Có TK 3389                       |  1.000.000
```

### 15. Trừ TNCN

```
Nợ TK 334                        | 1.600.000
Có TK 3335 — Thuế TNCN           | 1.600.000
```

### 16. Trả lương (sau khi trừ BH + TNCN)

```
Nợ TK 334                        | 87.900.000  (= 100tr - 10,5tr - 1,6tr)
Có TK 111/112                    | 87.900.000
```

## V. Khấu hao TSCĐ

### 17. Khấu hao tháng (đường thẳng)

```
Nợ TK 627  — CP SXC (TSCĐ SX)   |  3.000.000
Nợ TK 641  — CP bán hàng        |  1.500.000
Nợ TK 642  — CP QLDN             |  2.000.000
Có TK 214  — Hao mòn TSCĐ        |  6.500.000
```

## VI. Thuế

### 18. Nộp thuế GTGT

```
Nợ TK 3331                       | 20.000.000
Có TK 111/112                    | 20.000.000
```

### 19. Tạm tính TNDN quý

```
Nợ TK 8211 — CP thuế TNDN HH     | 50.000.000
Có TK 3334 — Thuế TNDN           | 50.000.000

(Khi nộp)
Nợ TK 3334                       | 50.000.000
Có TK 112                        | 50.000.000
```

## VII. Kết chuyển cuối kỳ

### 20. Kết chuyển doanh thu / chi phí xác định KQKD

```
Bước 1 — Kết chuyển doanh thu:
Nợ TK 511, 515, 711              
Có TK 911                        

Bước 2 — Kết chuyển chi phí:
Nợ TK 911                        
Có TK 632, 635, 641, 642, 811, 821

Bước 3 — Kết chuyển LN/LL:
- Nếu lãi:
  Nợ TK 911 / Có TK 421 (LNCPP)
- Nếu lỗ:
  Nợ TK 421 / Có TK 911
```

## Lưu ý quan trọng

- Số tiền ở ví dụ chỉ minh hoạ; thực tế dùng số thật từ chứng từ
- Kiểm tra tổng Nợ = tổng Có trước khi ghi sổ
- Mỗi bút toán phải có chứng từ gốc (HĐ, phiếu, hợp đồng)
- TK 627 = chi phí SX chung phân bổ vào giá thành; TK 641/642 = chi phí thời kỳ → vào BC KQKD

## Tham chiếu

- TT200/2014/TT-BTC ngày 22/12/2014
- TT53/2016/TT-BTC sửa đổi
- TT78/2014/TT-BTC hướng dẫn TNDN
