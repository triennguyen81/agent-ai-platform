---
name: knowledge-curator
description: |
  Format note đẹp và đầy đủ khi save-from-chat (lưu câu trả lời thành note).
  Kích hoạt khi user yêu cầu "lưu vào kiến thức" / "save this answer".
keywords:
  - lưu vào kiến thức
  - save this
  - lưu note
  - thêm vào knowledge
---

# Knowledge curator

Khi user yêu cầu lưu một câu trả lời thành note, bạn giúp **gọt** lại nội dung trước khi lưu — để Note sau này lookup được sạch sẽ.

## Cấu trúc note chuẩn

```markdown
# {Tiêu đề ngắn 5-12 từ — không dấu hỏi}

> _Nguồn: chat hỏi-đáp ngày YYYY-MM-DD_

## Tóm tắt
1-2 câu trả lời thẳng vào câu hỏi.

## Chi tiết
{Phần chính, bullet hoặc bảng}

## Tham chiếu
- {Note gốc nếu có, link, brand...}
- _Tags gợi ý: tag1, tag2, tag3_
```

## Tiêu đề

- **Không kết thúc bằng dấu hỏi** — Note là statement, không phải câu hỏi
- Trước: "Implant Straumann giá bao nhiêu, có những loại nào?" → Sau: "Bảng giá Implant Straumann theo dòng sản phẩm"
- Trước: "5 thời điểm rửa tay là gì?" → Sau: "WHO 5 Moments — 5 thời điểm rửa tay trong nha khoa"

## Tags gợi ý

Khi đề xuất, dùng 2-5 tag, mỗi tag 1-2 từ, lowercase:

- `implant`, `straumann`, `bảng-giá`, `2025`
- `tiệt-trùng`, `quy-trình`, `who`
- `niềng-răng`, `mắc-cài`, `so-sánh`

Tránh tag dài (`bang-gia-implant-straumann-2025`) → quá specific, khó tái dùng.

## Tránh

- Lưu nguyên câu trả lời dài 500 từ → cô đọng còn 150-200 từ
- Bỏ phần "Lưu ý:" / disclaimer → giữ lại
- Bỏ citation Note gốc → giữ luôn (để Note mới biết nó derive từ đâu)
