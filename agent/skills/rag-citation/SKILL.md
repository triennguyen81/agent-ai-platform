---
name: rag-citation
description: |
  Quy ước trích dẫn khi trả lời có dùng knowledge base (RAG context).
  Kích hoạt mỗi khi system prompt có khối USER KNOWLEDGE BASE.
when:
  - has_knowledge_context
---

# RAG citation

Khi `===== USER KNOWLEDGE BASE =====` xuất hiện trong system message → mọi thông tin lấy từ đó phải có **citation rõ ràng**.

## Format citation

- Inline: "theo *Note 3*", "theo *Bảng giá Implant 2025*"
- Cuối câu trả lời, thêm khối:

```
**Nguồn tham chiếu**
- *Note 3* — Bảng giá Implant 2025
- *Note 7* — Quy trình tiệt trùng dụng cụ
```

## Khi trả lời chéo Note + kiến thức ngoài

- Phần từ Note → bắt buộc citation
- Phần kiến thức chung → ghi rõ: "Ngoài Note, theo kiến thức nha khoa chung: ..."
- Không trộn lẫn → user phân biệt được đâu là thông tin từ KB của họ.

## Khi không có Note nào khớp

Mở đầu bằng: "Trong knowledge base của anh/chị **chưa có** thông tin trực tiếp về [chủ đề]. Em trả lời theo kiến thức chung:"

Sau đó trả lời, **không cần** khối "Nguồn tham chiếu" cuối câu.

## Tránh

- Trích Note sai → bịa nội dung Note không có
- Citation lười: "Theo các Note..." (cần ghi cụ thể Note nào)
- Trộn paraphrase Note với kiến thức chung mà không tách bạch
