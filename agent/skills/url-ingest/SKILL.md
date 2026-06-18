---
name: url-ingest
description: |
  Khi user yêu cầu nạp một URL (bài viết, tài liệu, trang sản phẩm)
  thành Note trong knowledge base. Dùng `fetch_url` để lấy nội dung,
  parse Markdown, rồi `save_note` để lưu.
keywords:
  - import url
  - nạp url
  - crawl
  - lấy bài
  - convert into note
  - chuyển thành note
---

# URL ingest

Khi user đưa URL (link bài viết, tài liệu, sản phẩm), bạn:

1. **Gọi `fetch_url(url)`** để lấy HTML/text.
2. **Trích nội dung chính** — bỏ navigation, ads, footer, comments.
3. **Format Markdown sạch** — heading, bullet, bảng nếu cần.
4. **Tự sinh tiêu đề ngắn** 5-12 từ (không phải URL).
5. **Tự sinh 2-5 tag** lowercase, kebab-case, theo chủ đề chính.
6. **Gọi `save_note(title, content, tags)`** để lưu.
7. **Trả lời ngắn** xác nhận: "Đã lưu Note: {title} ({len} chars, tags: …)".

## Khi URL trả về lỗi

- 4xx → báo "URL không truy cập được ({status})", hỏi user check lại link.
- 5xx → báo "Server đích đang lỗi, thử lại sau."
- Timeout → "URL phản hồi chậm, thử lại sau hoặc lưu thủ công."

## Khi nội dung quá dài

- Cap content ở **20,000 ký tự** đầu tiên.
- Thêm dòng cuối: `> _Trích từ {url} — nội dung gốc dài hơn, đã cắt._`

## Khi user đưa list nhiều URL

- Gọi `fetch_url` + `save_note` lần lượt cho từng URL.
- Cuối cùng trả 1 summary: "Đã lưu N Note: …".
- Nếu 1 URL fail → tiếp tục với URL còn lại, báo lỗi cụ thể trong summary.

## Không

- Không lưu URL trùng (nếu thấy title clash, append " (2)" thay vì ghi đè).
- Không bịa nội dung khi `fetch_url` fail — báo cho user biết và dừng.
- Không thêm comment của bản thân vào content — content là từ URL gốc.
