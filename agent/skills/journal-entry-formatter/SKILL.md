---
name: journal-entry-formatter
description: |
  Format chuẩn cho bút toán định khoản theo TT200. Kích hoạt khi user hỏi
  cách hạch toán, định khoản 1 nghiệp vụ cụ thể, hoặc nhập sai cần sửa.
keywords:
  - định khoản
  - hạch toán
  - bút toán
  - ghi sổ
  - sổ nhật ký
  - nhật ký chung
  - nhật ký mua hàng
  - nhật ký bán hàng
  - nợ tk
  - có tk
  - ghi nhận doanh thu
  - ghi nhận chi phí
  - mua hàng
  - bán hàng
  - chi tiền
  - thu tiền
  - chuyển khoản
  - lương
  - khấu hao
  - chiết khấu
---

# Journal Entry Formatter — Định khoản theo TT200

Chuẩn format khi trả lời câu hỏi "định khoản thế nào" cho 1 nghiệp vụ cụ thể.

## Cấu trúc bút toán đầy đủ

```
**Bút toán {N}:** {Mô tả nghiệp vụ 1 dòng — bao gồm bên A, bên B, số tiền, ngày nếu có}

- Nợ TK {xxx}   — {Tên TK đầy đủ}      | {Số tiền} ₫
- Nợ TK {xxx}   — {Tên TK đầy đủ}      | {Số tiền} ₫    *(nếu nhiều vế Nợ)*
- Có TK {xxx}   — {Tên TK đầy đủ}      | {Số tiền} ₫

*Cơ sở: {Hoá đơn / Phiếu / Chứng từ} — {Điều khoản TT200 liên quan nếu có}*
```

Kiểm tra **tổng Nợ = tổng Có** trước khi trả về. Nếu lệch → đó là lỗi, không phải bút toán cân.

## Khi 1 nghiệp vụ → 1 bút toán

Đơn giản nhất. Ví dụ "Mua hàng 110tr đã gồm VAT 10%, chưa trả":

```
**Bút toán:** Mua hàng nhập kho, chưa thanh toán cho nhà cung cấp X.

- Nợ TK 156   — Hàng hoá                 | 100.000.000 ₫
- Nợ TK 133   — Thuế GTGT đầu vào        |  10.000.000 ₫
- Có TK 331   — Phải trả cho người bán   | 110.000.000 ₫

*Cơ sở: Hoá đơn GTGT — TT200 Điều 18.*
```

## Khi 1 nghiệp vụ → nhiều bút toán

Ví dụ "Bán hàng + xuất kho" → 2 bút toán riêng (ghi doanh thu + giá vốn):

```
**Bút toán 1:** Ghi nhận doanh thu bán hàng (chưa thu tiền).

- Nợ TK 131   — Phải thu khách hàng      | 220.000.000 ₫
- Có TK 511   — Doanh thu bán hàng       | 200.000.000 ₫
- Có TK 3331  — Thuế GTGT đầu ra         |  20.000.000 ₫

**Bút toán 2:** Ghi nhận giá vốn hàng bán.

- Nợ TK 632   — Giá vốn hàng bán         | 150.000.000 ₫
- Có TK 156   — Hàng hoá                 | 150.000.000 ₫
```

Đánh số `Bút toán 1, 2, 3...`. Không gộp doanh thu + giá vốn vào 1 bút toán.

## Tách dòng VAT (BẮT BUỘC)

VAT luôn ở dòng riêng — không cộng vào TK chính.

- ✅ Đúng: `Nợ TK 156 100tr` + `Nợ TK 133 10tr` + `Có TK 331 110tr`
- ❌ Sai: `Nợ TK 156 110tr (đã gồm VAT) / Có TK 331 110tr`

Lý do: bảo đảm khấu trừ VAT đúng và đối chiếu được với tờ khai 01/GTGT.

## Khi user nhập case ambiguous

Hỏi lại 1 câu cụ thể trước khi định khoản:

| Câu hỏi user | Hỏi lại |
|---|---|
| "Mua máy 50tr định khoản sao?" | "Máy này có là tài sản cố định không? (giá trên 30tr + thời gian sử dụng >1 năm → TK 211; ngược lại → TK 142/242 hoặc TK 153)" |
| "Trả lương 100tr định khoản sao?" | "Anh/chị đã trừ BHXH/BHYT/BHTN của người lao động chưa, hay 100tr là tổng quỹ?" |
| "Mua nguyên vật liệu 30tr" | "Có hoá đơn VAT không, hay hoá đơn bán lẻ? Có VAT thì tách TK 133, không thì gộp vào TK 152." |

## Tổng hợp các TK Nợ–Có thường dùng (cheat-sheet để tra nhanh)

| Nghiệp vụ | TK Nợ | TK Có |
|---|---|---|
| Mua hàng / NVL chưa trả | 156/152 + 133 | 331 |
| Bán hàng chưa thu | 131 | 511 + 3331 |
| Xuất kho ghi giá vốn | 632 | 156/155 |
| Trả lương cho NV | 334 | 111/112 |
| Trích BHXH (DN trả) | 642 (BHXH DN) | 338 |
| Khấu hao TSCĐ tháng | 627/641/642 (tuỳ bộ phận) | 214 |
| Nộp thuế GTGT | 3331 | 111/112 |
| Trả nhà cung cấp | 331 | 111/112 |
| Thu nợ khách hàng | 111/112 | 131 |
| Tạm ứng cho NV đi công tác | 141 | 111/112 |
| Hoàn ứng (NV mua hàng) | 156/152/642 (+ 133) | 141 |
| Chiết khấu thanh toán cho khách (chi phí TC) | 635 | 131 |
| Chiết khấu thương mại cho khách | 5211 | 131 |
| Trích trước chi phí phải trả | 642/635 | 335 |

## Tránh

- Viết "Nợ X / Có Y: số" trên 1 dòng — luôn 2 dòng riêng
- Bỏ tên TK (chỉ ghi số) — phải có tên đầy đủ ở dòng đầu khi giới thiệu
- Tự "phỏng đoán" số tiền khi user chưa cung cấp — phải hỏi
- Bỏ qua tham chiếu chứng từ — luôn ghi cơ sở (hoá đơn, phiếu, hợp đồng)
