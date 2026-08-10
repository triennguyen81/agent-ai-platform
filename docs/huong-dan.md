# AIQ — Hướng dẫn demo cho anh Toàn (BSN)

**Cập nhật:** 2026-08-11 · **Người triển khai:** Tuong Nguyenanh
**Sản phẩm:** AIQ Agentic AI Platform · **Pilot:** BSN Strategic Solutions Co., Ltd

---

## 🎯 Nguyên tắc demo (45 phút tổng)

1. **Show value trước, tech sau** — bắt đầu từ use case cụ thể BSN quan tâm (kế toán, M&A vì đúng mảng BSN mạnh).
2. **3 demo × 10-15 phút mỗi cái** — không show hết cùng lúc, phân đoạn rõ.
3. **Anh Toàn tự bấm** — em screen-share/call chỉ hướng dẫn, để anh feel ownership.
4. **Chốt 1 câu hỏi feedback** cuối mỗi demo, không hỏi tất cả 1 lúc.

---

## 📇 Thông tin đăng nhập

| Field | Value |
|---|---|
| **URL** | https://app.cattuong.ai/login |
| **Admin BSN** | `admin@bsn.vn` / `BSN@2026` |
| **Nhân viên 1** | `user1@bsn.vn` / `BSN@2026` |
| **Nhân viên 2** | `user2@bsn.vn` / `BSN@2026` |
| **Nhân viên 3** | `user3@bsn.vn` / `BSN@2026` |

**Đề xuất:** login `user1@bsn.vn` trước (trải nghiệm nhân viên) → chuyển `admin@bsn.vn` xem quản trị.

---

## 🎬 Kịch bản demo — 4 phần

### Phần 1 — BSN Assistant (10 phút)

**Trợ lý AI cho nhân viên BSN tra cứu thông tin công ty, dịch vụ, quy trình.**

**Bước 1:** login `user1@bsn.vn`
**Bước 2:** menu **Chat** → chọn agent **"Trợ lý AI BSN"**
**Bước 3:** gõ 5 câu (theo thứ tự dễ → khó):

1. `BSN có văn phòng ở đâu? Hotline là gì?`
2. `Tôi cần thành lập công ty FDI ngành F&B, BSN hỗ trợ những gì?`
3. `Phí dịch vụ kế toán hàng tháng cho SME khoảng bao nhiêu?`
4. `Startup Series A gọi vốn 2 triệu USD, cần BSN làm gì?`
5. `BSN khác gì các công ty kế toán bình thường?`

**Câu hỏi feedback:**
> "Trong 5 câu, có bao nhiêu câu trả lời chính xác thông tin BSN thực tế? Câu nào cần sửa nội dung?"

---

### Phần 2 — Chứng từ kế toán qua ảnh (15 phút) ⭐ USE CASE KHÁC BIỆT

**Upload ảnh hoá đơn VAT → AI OCR + trích xuất fields + đề xuất bút toán TT200.**

**Chuẩn bị:** 1 ảnh hoá đơn VAT thật của khách BSN (chụp phone OK, che thông tin nhạy nếu cần).

**Bước 1:** login `admin@bsn.vn` → menu **Chứng từ** → **Thêm chứng từ**
**Bước 2:** kéo thả ảnh vào dropzone → chọn loại **"Hoá đơn VAT điện tử"** → bấm **Chạy OCR + AI extract**
**Bước 3:** chờ ~15-30 giây → thấy row voucher mới, status **"Đã trích xuất"**
**Bước 4:** click vào voucher → trang detail hiện:
  - Ảnh gốc bên trái (thumbnail có thể click để zoom)
  - Fields extracted: số HĐ, ký hiệu, ngày, MST bên bán/mua, tổng tiền, VAT
  - Bảng chi tiết dòng hàng
  - **Bút toán N/C đề xuất theo TT200** (VD: Nợ 156 + Nợ 1331 / Có 331)

**Bước 5 (chỉ show, không cần làm):** bấm "Xuất Excel" → download `.xlsx` để import Misa/Fast/Bravo

**Câu hỏi feedback:**
> "Anh Toàn nghĩ nhân viên kế toán BSN có tiết kiệm được thời gian không? Nếu 1 kế toán viên xử lý 200 hoá đơn/tháng, gói phí bao nhiêu là fair?"

---

### Phần 3 — M&A / IB Advisor (15 phút) ⭐ USE CASE CAO CẤP

**Trợ lý M&A / Investment Banking cho BSN M&A practice — 32 skills chuyên sâu (21 VN-context + 10 IB global standard).**

**Bước 1:** login `admin@bsn.vn` → menu **Chat** → chọn agent **"BSN M&A / Investment Banking Advisor"**
**Bước 2:** gõ 3 câu (chọn 1-2 tuỳ thời gian):

1. `Định giá công ty F&B chuỗi cà phê 200 điểm, EBITDA 20 tỷ VND. Dùng cả DCF và Comparable analysis. Multiple ngành F&B VN như nào?`
2. `Deal M&A sell-side cho SME sản xuất VN mất bao lâu? Timeline chi tiết từng phase.`
3. `Chuẩn bị investment committee Q&A prep cho deal PE Việt Nam đang định giá 50 triệu USD.`

**Chú ý:** agent này dùng Claude Sonnet 4.6 (mạnh hơn Haiku) → response chi tiết hơn, có tables + follow-up questions.

**Câu hỏi feedback:**
> "Chất lượng câu trả lời với 32 skills IB (21 VN + 10 global) — đủ để BSN dùng làm foundation cho M&A advisory không?"

---

### Phần 4 — Admin console (5 phút) — "BSN tự quản trị"

**Show nhanh 4 menu quản trị để BSN thấy có thể tự vận hành, không cần dev.**

**Login:** `admin@bsn.vn`

1. **Knowledge** — xem 4 tài liệu BSN (Giới thiệu / Dịch vụ / Ngành / FAQ). Click 1 tài liệu → sửa nội dung (VD sửa hotline) → Save → hỏi lại nhân viên → agent trả với thông tin mới ngay.

2. **Agents** — xem 2 agents cho BSN:
   - `bsn-assistant` (Haiku, chat cơ bản)
   - `bsn-ma-advisor` (Sonnet 4.6, 32 skills IB)
   Click vào agent → xem cấu hình: lifecycle, tier, tools grant, knowledge notes attached.

3. **Users** — thấy 4 tài khoản BSN. Click "Gán agent" để control ai được dùng agent nào.

4. **Reviews / HITL Queue** — nếu muốn tạo agent mới, quy trình phải qua review board trước khi lên production.

**Câu hỏi feedback:**
> "BSN có thể tự vận hành không cần dev, đúng không? Có menu nào rối/khó hiểu?"

---

## 📊 Nội dung đã cấu hình sẵn cho BSN

### 4 tài liệu Knowledge (~12,600 ký tự tổng)

| Tài liệu | Nội dung |
|---|---|
| Giới thiệu công ty BSN | Tên đầy đủ · địa chỉ 2 văn phòng · hotline · email · năng lực (10+ năm, 200+ DN) · 5 điểm khác biệt · phương pháp 4 bước |
| Danh mục 9 dịch vụ | Thành lập DN · Kế toán-Thuế · Kiểm toán · Tuân thủ · Nhân sự · Tư vấn DN · M&A · Visa · F&B (chi tiết từng mảng) |
| Ngành phục vụ + Phương pháp | 8 ngành (Sản xuất · Nông nghiệp · BĐS · TC-NH · FDI · TM-DV · IT · GD-YT) + quy trình Discovery → Strategy → Execution → Evaluate |
| FAQ | 12 câu hỏi thường gặp về dịch vụ, phí, khác biệt với kế toán thường, phục vụ FDI/startup, cách đặt lịch, thanh toán |

### 2 agents chuyên biệt

- **bsn-assistant** — Q&A tổng quát về BSN, dùng 4 KB notes ở trên, model Haiku 4.5 (nhanh, tiết kiệm)
- **bsn-ma-advisor** — 32 skills M&A/IB (21 ma-* VN-context + 10 ib-* global), model Sonnet 4.6 (mạnh hơn), + 4 KB notes riêng: BSN fee table, VN Legal Cheat Sheet, Notable VN Deals 2020-2025, VN+EN Glossary

### Voucher processing pipeline

- Upload image/PDF hoá đơn VAT → PaddleOCR (Vietnamese) → Claude extract JSON → save vào Postgres → đề xuất bút toán N/C theo TT200
- 5 loại chứng từ hỗ trợ: hoá đơn VAT · phiếu thu · phiếu chi · uỷ nhiệm chi · sao kê ngân hàng (Phase 1 chính thức hoá đơn VAT, phase 2 mở rộng khi BSN yêu cầu)
- File lưu trên object storage S3 (không mất khi container rebuild)

---

## ⚠️ Giới hạn kỹ thuật (transparent)

| Item | Giới hạn |
|---|---|
| Rate limit LLM | Claude Max subscription ~50 message / 5 giờ (đủ cho demo 3-5 nhân viên; nếu > 10 người cùng lúc phải upgrade) |
| OCR accuracy | PaddleOCR VN ~85-92% cho hoá đơn rõ nét; giảm nếu ảnh mờ, font phức tạp, hoá đơn cũ |
| Chat memory | 200 messages / thread (đủ cho hầu hết use case) |
| Upload size | ≤ 25MB / file |
| Data storage | VN (Contabo VPS, không share offshore trừ prompts sang Anthropic API) |

---

## 🔒 Bảo mật + Data

- **Data lưu ở VN** (Contabo cosmos VPS)
- **Không share Anthropic** — chỉ prompts được gửi qua API để LLM xử lý (không lưu bên Anthropic > 30 ngày theo ToS)
- **Export data:** admin có thể download tất cả voucher/journal xuống Excel
- **Rotate password:** admin có thể đổi trực tiếp qua UI (menu "Users" → chọn user → "Đổi mật khẩu")
- **File uploads:** lưu trên MinIO object storage (S3-compatible), có thể migrate sang AWS S3 / Cloudflare R2 sau nếu cần
- **Isolation:** container hardening (read-only filesystem, không có capabilities OS thừa, outbound network chặn — chỉ reach gateway/agent nội bộ)

---

## 📈 Monitoring + Health

- **Health endpoints:** `https://app.cattuong.ai/api/health` (web) · gateway `/healthz/live` (internal)
- **Uptime hiện tại:** 67+ ngày (từ tháng 6 đến nay)
- **Grafana dashboard:** có (SSH tunnel để anh Toàn dev truy cập nếu cần)
- **Log retention:** 15 ngày (Loki)

---

## 📝 Câu hỏi feedback chốt sau demo (gửi email)

Xin anh Toàn cho phản hồi 5 câu:

1. **BSN Assistant:** có bao nhiêu câu / 5 trả lời đúng thông tin BSN thực tế?
2. **Voucher extract:** nhân viên kế toán BSN tiết kiệm bao nhiêu thời gian/hoá đơn? Có gì thiếu?
3. **M&A Advisor:** dùng làm foundation cho M&A practice của BSN OK không? Skill nào cần bổ sung?
4. **Admin console:** có cần training thêm không? Menu nào rối/khó hiểu?
5. **Triển khai thật:** BSN có muốn pilot 4 tuần với 3-5 nhân viên nội bộ không? Gói phí phù hợp bao nhiêu?

---

## 🚀 Roadmap nếu BSN triển khai thật (4 tuần)

| Tuần | Việc | BSN cần chuẩn bị |
|---|---|---|
| 1 | Setup tenant riêng, whitelabel branding BSN, cấu hình quyền theo phòng ban | Logo · màu chủ đạo · cơ cấu phòng ban |
| 2 | Nạp toàn bộ tài liệu nội bộ BSN (SOP · mẫu HĐ · bảng giá chi tiết · case study) | 10-30 tài liệu Word/PDF nội bộ |
| 3 | Onboard 5-10 nhân viên BSN + đào tạo admin 1 buổi | Danh sách người dùng + email |
| 4 | Chạy pilot, thu feedback, sửa lỗi, tinh chỉnh câu trả lời | 1 người điều phối feedback (30 phút/ngày) |

**Sau pilot:** mở rộng voucher Phase 2 (3 loại chứng từ khác) · Voucher Phase 3 (auto-post ERP Misa/Fast/Bravo) · HITL approval workflow · Multi-tenant nếu BSN muốn resell cho khách của BSN.

---

## 📞 Liên hệ

**Người triển khai:** Tuong Nguyenanh
**Vai trò:** Founder & CTO — AIQ Agentic AI Platform
**Email:** tuong247@gmail.com

Mọi câu hỏi kỹ thuật, đề xuất tính năng, hoặc thảo luận thương mại — sẵn sàng trao đổi 1:1 qua Zalo / Google Meet.

---

*Cảm ơn anh Toàn đã dành thời gian trải nghiệm sản phẩm.*
