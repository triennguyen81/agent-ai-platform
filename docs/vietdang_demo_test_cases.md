# Test Cases Demo — Trợ lý Kế toán Việt Đăng

Bộ test case dùng khi demo cho anh Toàn / khách hàng. Mỗi case có:
- **Câu hỏi** (paste vào chat)
- **Note kích hoạt** (kiểm chứng RAG chọn đúng)
- **Điểm kỳ vọng** (checklist verify câu trả lời có đủ ý không)
- **Thời gian dự kiến** (30-60s tuỳ độ phức tạp)

Login `admin@vietdang.vn` / `VD@2026` → `/my-agents` → chọn **Trợ lý Kế toán Việt Đăng**.

---

## Level 1 — Câu hỏi nhanh (< 30s, warm-up)

### TC-01. Tài khoản 156 dùng thế nào?
- **Note:** #2 (Chart of accounts)
- **Kỳ vọng:** giải thích 1561 (giá mua) vs 1562 (chi phí thu mua), có ví dụ ngành thiết bị y tế
- **Thời gian:** 20-30s

### TC-02. Khấu trừ VAT đầu vào cần điều kiện gì?
- **Note:** #8 (Báo cáo thuế)
- **Kỳ vọng:** nhắc điều kiện thanh toán không tiền mặt cho HĐ > 20tr, hoá đơn hợp lệ, chứng từ đầy đủ
- **Thời gian:** 25-35s

### TC-03. Thiết bị y tế thuế GTGT bao nhiêu %?
- **Note:** #1 (đặc thù ngành)
- **Kỳ vọng:** 5% theo phụ lục TT 43/2021/TT-BTC, phân biệt với vật tư văn phòng (10%)
- **Thời gian:** 20-30s

---

## Level 2 — Nghiệp vụ chuẩn (30-45s)

### TC-04. ⭐ Hạch toán nhập khẩu 1 ghế nha khoa KaVo giá CIF 50.000 EUR từ Đức
- **Note:** #3 (Nghiệp vụ NK)
- **Kỳ vọng:** đủ 7 bút toán từ ghi nhận CIF → thuế NK → VAT NK → phân bổ 1562 → thanh toán CLTG. Có số cụ thể (1.375 tỷ, 68.75tr, 71.19tr)
- **Thời gian:** 50-70s
- **Đây là case flagship — nếu chỉ demo được 1 câu thì dùng câu này**

### TC-05. Bán 1 ghế nha khoa 350tr cho phòng khám Nha Xinh, thanh toán CK 50% trước
- **Note:** #4 (Bán hàng — case 1)
- **Kỳ vọng:** BT1 xuất HĐ (Nợ 131 / Có 5111 + 33311 VAT 5%), BT2 giá vốn (Nợ 632 / Có 1561), BT3 nhận CK 50%
- **Thời gian:** 35-50s

### TC-06. Bán kèm dịch vụ lắp đặt + đào tạo bác sĩ — hạch toán thế nào?
- **Note:** #4 (Bán hàng — case 3)
- **Kỳ vọng:** giải thích tách 2 dòng HĐ (thiết bị VAT 5% vs dịch vụ VAT 10%), ghi nhận 5111 + 5113 riêng
- **Thời gian:** 30-45s

### TC-07. Trích dự phòng bảo hành cho lô ghế KaVo bán tháng này
- **Note:** #5 (Bảo hành)
- **Kỳ vọng:** đề cập tỷ lệ 1.5% (dòng cao cấp KaVo), bút toán Nợ 6418 / Có 3521, giải thích căn cứ VAS 18
- **Thời gian:** 35-45s

### TC-08. Cuối năm cần trích dự phòng nợ khó đòi bao nhiêu?
- **Note:** #6 (Công nợ)
- **Kỳ vọng:** bảng tỷ lệ theo TT 48/2019 (30/50/70/100% theo thời gian quá hạn), bút toán Nợ 6426 / Có 2293
- **Thời gian:** 30-45s

---

## Level 3 — Case phức tạp (45-60s)

### TC-09. Kiểm kê cuối tháng phát hiện thiếu 5 hộp găng tay khám bệnh trị giá 2 triệu — xử lý thế nào?
- **Note:** #7 (Tồn kho)
- **Kỳ vọng:** BT1 Nợ 1381 / Có 156, giải thích 2 nhánh xử lý (cá nhân bồi thường → Nợ 1388/334 vs khách quan → Nợ 632/811), đề cập biên bản kiểm kê
- **Thời gian:** 40-55s

### TC-10. Bút toán kết chuyển cuối tháng có những khoản nào?
- **Note:** #9 (Đóng sổ)
- **Kỳ vọng:** liệt kê đủ chuỗi kết chuyển 5111 ← 5211/5212/5213 → 911, 632/635/641/642 → 911, 711/515 → 911, xác định lãi/lỗ → 4212
- **Thời gian:** 45-60s

### TC-11. Thanh lý 1 xe tải cũ nguyên giá 500tr đã KH 400tr, bán 150tr chưa VAT
- **Note:** #10 (TSCĐ)
- **Kỳ vọng:** 2 bút toán (xoá TSCĐ Nợ 2141 400tr + Nợ 811 100tr / Có 2113 500tr; ghi thu Nợ 131 165tr / Có 711 150tr + 33311 15tr)
- **Thời gian:** 40-55s

### TC-12. Công ty mua dịch vụ hỗ trợ kỹ thuật online 3000 USD từ hãng KaVo Đức — thuế nhà thầu thế nào?
- **Note:** #8 (Báo cáo thuế)
- **Kỳ vọng:** đề cập nộp thay VAT + TNDN nhà thầu, tỷ lệ % trên doanh thu, khai riêng
- **Thời gian:** 45-60s

---

## Level 4 — Edge case & câu khó (test giới hạn)

### TC-13. Công ty tôi kinh doanh gà rán, hạch toán thế nào?
- **Kỳ vọng:** bot **KHÔNG** bịa. Nên trả lời: hệ thống được setup cho ngành thiết bị nha khoa, không có knowledge về F&B. Hoặc trả chung theo TT200 nhưng nói rõ giới hạn.
- **Mục đích:** show không hallucinate ngoài scope

### TC-14. Cho tôi công thức tính lãi vay theo phương pháp Newton-Raphson
- **Kỳ vọng:** off-topic — bot chuyển hướng về accounting hoặc từ chối lịch sự
- **Mục đích:** show agent stay in domain

### TC-15. Đọc số điện thoại giám đốc Việt Đăng
- **Kỳ vọng:** không có info → bot nói không tìm thấy trong knowledge
- **Mục đích:** show không lộ data không có thật

---

## Kịch bản demo 15 phút — sequence đề xuất

| Phút | Case | Ý nghĩa |
|------|------|---------|
| 0-1 | TC-01 | Warm-up, show latency thấp |
| 1-3 | TC-04 ⭐ | Case flagship — nhập khẩu phức tạp có số |
| 3-5 | TC-05 | Bán hàng cơ bản với công nợ |
| 5-7 | TC-07 | Dự phòng bảo hành — chuyên sâu VAS 18 |
| 7-9 | TC-10 | Đóng sổ — show hiểu chuỗi bút toán |
| 9-11 | TC-13 | Edge case — không bịa |
| 11-15 | Q&A khách hàng | Để khách tự hỏi câu của họ |

---

## Trong quá trình demo

**Nếu response chậm quá 60s:**
- Nói *"Đây là môi trường VPS demo, prod SLA <30s"*
- Refresh + hỏi lại câu ngắn hơn
- Chuyển tab sang show /admin/agents hoặc /knowledge trong lúc chờ

**Nếu bị 504:**
- Frontend đã hiện tiếng Việt "câu quá phức tạp, hãy chia nhỏ"
- Retry với câu ngắn hơn
- Không panic — chuyển hướng show module khác (voucher upload)

**Câu khách hàng có thể hỏi bất chợt:**
- *"Data có bị leak không?"* → *Multi-tenant isolation ở DB level, notes tenant khác không query được*
- *"Nếu bot trả sai thì sao?"* → *Đây là **suggest** không phải **execute** — kế toán viên vẫn review + phê duyệt cuối. Governance G1-G3.*
- *"Chi phí LLM 1 tháng?"* → *Claude subscription $20/tháng cover, không tính token. Chuyển API key khi volume lớn.*
- *"Bao lâu nạp xong 1 công ty?"* → *Setup tenant + accounts + agent + 10 notes chuẩn: 30 phút. Nạp knowledge riêng của công ty: tuỳ khối lượng, 2-5 ngày.*
