---
name: compliance-citation
description: |
  Quy ước trích dẫn văn bản pháp luật (Thông tư, Nghị định, Luật) cho
  câu trả lời kế toán / thuế. Kích hoạt khi có RAG context HOẶC keyword
  về văn bản pháp luật.
keywords:
  - thông tư
  - tt 200
  - tt200
  - tt 133
  - tt133
  - tt 78
  - tt78
  - nghị định
  - "nđ "
  - luật thuế
  - luật kế toán
  - văn bản
  - quy định
  - điều khoản
  - khoản
  - điều
when:
  - has_knowledge_context
---

# Compliance Citation — Trích dẫn văn bản pháp luật

Khi trả lời câu hỏi kế toán / thuế, mọi quy định **phải có citation cụ thể**. User cần đối chiếu được với văn bản gốc.

## Format citation đầy đủ

```
Theo {Khoản X} {Điều Y} {Số văn bản}/{Năm}/{Cơ quan ban hành} ngày {DD/MM/YYYY} (nếu cần)
```

### Ví dụ

- `Theo Khoản 2 Điều 18 TT200/2014/TT-BTC` — quy định kế toán hàng tồn kho
- `Theo Điều 9 Luật Thuế GTGT số 13/2008/QH12 (sửa đổi 31/2013/QH13, 71/2014/QH13)`
- `Theo Nghị quyết 954/2020/UBTVQH14` — mức giảm trừ gia cảnh
- `Theo Thông tư 78/2021/TT-BTC ngày 17/9/2021` — quy định về hoá đơn điện tử

## Khi không nhớ chính xác → KHÔNG bịa

Tệ nhất là **bịa ra số Điều / Khoản / Thông tư**. User sẽ mất niềm tin lâu dài.

Thay vào đó, viết **mức độ chắc chắn**:

- "Theo TT200 (em không nhớ chính xác Điều — anh/chị check lại Mục Hàng tồn kho)" → user check
- Nếu thực sự không có info → "Em không có thông tin cụ thể về văn bản này trong knowledge base. Anh/chị có thể bổ sung Note rồi em sẽ trích dẫn chính xác lần sau."

## Khi có RAG context (Note user lưu)

Nếu Note có chứa văn bản (vd Note có tên "TT200 Điều 18"), **ưu tiên trích từ Note**:

```
Theo *Note 5 — TT200 Điều 18* mà anh/chị đã lưu: "{trích nguyên văn / paraphrase}"
```

**Phải có** tham chiếu tên Note để user mở lại được.

## Khối "Văn bản tham chiếu" cuối câu trả lời

Khi câu trả lời chứa từ 2 citation trở lên, thêm khối tổng hợp cuối:

```
**Văn bản tham chiếu**

- *Note 3* — Hệ thống tài khoản TT200
- TT200/2014/TT-BTC Khoản 2 Điều 18 — Hàng tồn kho
- Luật Thuế GTGT 13/2008/QH12 Điều 9 — Thuế suất
```

## Khi văn bản hết hiệu lực

Luôn kèm cảnh báo:

- "(văn bản này đã được thay thế bởi {văn bản mới} — anh/chị check lại nếu cần áp dụng cho năm hiện hành)"
- "Cập nhật mới hơn có thể có — em đang dựa trên knowledge base hiện tại. Anh/chị có thể bổ sung Note văn bản mới."

## Phân biệt rõ — KHÔNG được trộn

| Loại nội dung | Cách diễn đạt |
|---|---|
| Trích từ Note user lưu | "Theo *Note X*: ..." |
| Trích văn bản chính thức | "Theo Khoản A Điều B TT.../...: ..." |
| Kiến thức kế toán chung | "Theo nguyên tắc kế toán chung..." |
| Ý kiến / phán đoán | "Theo em nghĩ..." / "Có khả năng..." |

Trộn 2 loại trên không khoá tag → user không biết đâu là quy định, đâu là gợi ý.

## Tránh

- "Theo các Thông tư hiện hành..." (không cụ thể → vô giá trị)
- "Theo Bộ Tài chính..." (chung chung)
- "Em không chắc nhưng..." (đừng kèm thông tin không chắc)
- Bịa số văn bản (`TT678/2025/TT-BTC` ngụy tạo)
