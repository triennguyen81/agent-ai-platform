# Cheat sheet demo anh Toàn — 2026-08-17

## Login

```
URL:      https://app.cattuong.ai
Email:    admin@vietdang.vn
Password: VD@2026
```

## 5 URL bookmark sẵn — mở tab riêng từng cái

### ⭐ Case flagship — L2 approval
```
https://app.cattuong.ai/vouchers/fa004197-f7fe-4275-8060-12a669180dcb
```
**Nói:** *"Hoá đơn bán máy X-quang Sirona 651 triệu cho Bệnh viện RHM TW. Hệ thống tự động phát hiện: BV đang có công nợ 1.25 tỷ, sau HĐ này lên 1.9 tỷ = 95% hạn mức, có nợ quá hạn — bật cờ đỏ 'Cần Kế toán trưởng duyệt cấp 2'. Kế toán viên không được tự chấp nhận HĐ này."*

**Điểm nhấn show:** 3 badges đỏ + badge cam "Cần duyệt cấp 2"

### Case công nợ có quá hạn
```
https://app.cattuong.ai/vouchers/7226bac6-61c8-4eca-a416-3856b74f166e
```
**Nói:** *"Bán vật tư 31.5tr cho Phòng khám Nha Xinh. AI tự tra: KH này đang có 1 hoá đơn 367.5tr quá hạn 1-30 ngày. Bảng aging đầy đủ, kế toán viên biết ngay để nhắc thu."*

### Case phiếu thu (thu tiền công nợ)
```
https://app.cattuong.ai/vouchers/5e69313d-9d3f-462a-a967-f4eef08af576
```
**Nói:** *"Phiếu thu 183.75tr — 50% hoá đơn 00001101 của Nha Xinh. Hệ thống tự đề xuất bút toán N111/C131 để giảm công nợ khách."*

### Case KH clean (khách chưa có nợ)
```
https://app.cattuong.ai/vouchers/611cf1fb-4c60-483d-bff7-87cb22cc889c
```
**Nói:** *"Bán 15tr cho Phòng khám Đông Á — khách mới chưa có công nợ. Panel màu xanh: hạn mức 300M, đang dùng 5%, an toàn để bán tiếp."*

### Case A1 alt-flow — KH chưa có trong hệ thống
```
https://app.cattuong.ai/vouchers/7109ea15-0376-4d1e-a8fd-4e07f5f05d94
```
**Nói:** *"Uỷ nhiệm chi USD gửi hãng KaVo Đức — hãng này không phải khách nội bộ. Hệ thống báo panel amber 'Khách hàng chưa có trong hệ thống' — AI KHÔNG bịa data. Kế toán viên xem xét tạo NCC mới."*

## Kịch bản 5 phút (đủ thời gian không lãng phí)

1. **Login** (10s) → landing `/chat`
2. **Show nav** (20s) — *"Anh xem menu: Chat, Trợ lý, Nghiệp vụ dropdown, Workspace dropdown"*
3. **Vào `/vouchers`** (30s) — *"Đây là toàn bộ chứng từ đã upload. Có filter theo status, search, sort theo cột, phân trang 25/50/100"*
4. **Click voucher BV RHM** (2 phút) — case flagship. Kể chuyện từ "AI trích fields tự động" → "tra hệ thống công nợ" → "phát hiện rủi ro" → "chặn kế toán viên duyệt một mình"
5. **Click 1 case khác** (1 phút) — Nha Xinh hoặc Phiếu thu
6. **Vào `/customers`** (30s) — *"Đây là master khách hàng với AR aging, dùng chung cho AI khi phân tích"*
7. **Q&A** — để anh Toàn hỏi

## Câu hỏi anh Toàn có thể hỏi + đáp

| Câu | Trả lời |
|-----|---------|
| *"Live upload luôn được không?"* | *"Được, nhưng OCR mất 30-90s tuỳ ảnh. Em preload 5 case này để show flow không phải chờ."* |
| *"Chuyển sang Misa được không?"* | *"Xuất Excel format Misa import-ready — em đang build (Phase 1.5, 1-2h)."* |
| *"Bút toán suggest có accept được không?"* | *"Có, đang finalize UI nút Accept — Phase 1.5. Data đã sẵn, chỉ thiếu button."* |
| *"KH thật khác data này thì sao?"* | *"Đây là simulator để demo. Prod dùng MCP adapter kéo từ Misa/Fast/SAP thật — cùng cấu trúc, chỉ đổi nguồn."* |
| *"AI trả sai ai chịu?"* | *"AI chỉ **suggest**, kế toán viên duyệt cuối. Governance G1-G3 chống rủi ro."* |
| *"Chi phí?"* | *"Claude subscription $20/tháng cover toàn bộ token. Cost/hoá đơn = 0."* |

## RULE khi demo

- **KHÔNG upload live** — dùng 5 URL bookmark
- **KHÔNG mở IDE / terminal** — chỉ browser
- **NẾU có bug** — refresh trang, chuyển tab khác
- **Nói tự nhiên tiếng Việt** — không đọc slide

## Backup nếu app down

- URL trực tiếp cosmos: `http://cosmos:3000/vouchers/...` (bypass CF)
- Screenshot 5 case đã save tại `docs/demo-templates/*.png`
- Nếu app hoàn toàn die: show git commit log + screenshot làm bằng chứng
