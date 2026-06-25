# BÁO CÁO CHI PHÍ — Triển khai AIQ cho 6 users

**Dự án:** AIQ Agentic AI — Trợ lý AI cá nhân hoá cho doanh nghiệp
**Ngày báo cáo:** 25/06/2026
**Quy mô:** 6 users
**Hạ tầng đề xuất:** Ubuntu Server 24GB RAM

---

## 1. Tóm tắt điều hành

Báo cáo này ước tính **chi phí vận hành hằng năm** cho hệ thống AIQ Agentic AI phục vụ **6 người dùng** doanh nghiệp Việt Nam, bao gồm:

- Hạ tầng máy chủ Ubuntu (Cloud VPS 24GB RAM)
- Phí API Anthropic Claude (3 mức model: Haiku / Sonnet / Opus)
- Tên miền + Cloudflare + Backup

### Kết luận chính

| Phương án | Tổng chi phí / năm | / user / tháng | Khuyến nghị |
|---|---|---|---|
| **Tiết kiệm nhất** (Haiku + Smart routing + cache) | **~21 triệu VND** | 320k VND | Phù hợp khi pilot |
| **Cân bằng** (Sonnet + Smart + cache) | **~35 triệu VND** | 510k VND | ⭐ Khuyến nghị |
| **Chất lượng cao** (Sonnet-only) | ~64 triệu VND | 915k VND | Khi cần chất lượng đồng đều |
| **Tối đa** (Opus-only) | ~293 triệu VND | 4.1tr VND | Không khuyến nghị |

→ So với phần mềm kế toán enterprise (MISA SME ~30-60tr/năm cho cùng quy mô, **không có AI**), AIQ rẻ hơn **3-5 lần** với năng lực AI đầy đủ.

---

## 2. Hạ tầng máy chủ — VPS Ubuntu 24GB RAM

### So sánh nhà cung cấp

| Provider | Spec | $/tháng | Đặc điểm |
|---|---|---|---|
| **Contabo Cloud VPS L (đã chốt)** ⭐ | 8 vCPU · 24GB · 400GB NVMe · 32TB traffic | **$16** | Đã chốt — best price/perf |
| Contabo Cloud VPS XL | 10 vCPU · 24GB · 600GB NVMe | $23 | Upgrade khi cần disk lớn hơn |
| Hetzner CCX33 | 8 vCPU dedicated · 32GB | $58 | EU data, dedicated CPU |
| Vultr High Performance | 8 vCPU · 24GB | $96 | Global PoP |
| DigitalOcean Memory-optimized | 4 vCPU · 32GB | $172 | Quá đắt cho use case này |

**Đã chốt:** Contabo Cloud VPS L ($16/tháng) — 8 vCPU + 24GB + 400GB NVMe, đủ chạy toàn bộ stack (10 services Docker).

### Chi phí hạ tầng hằng năm

| Hạng mục | $/tháng | $/năm | VND/năm |
|---|---|---|---|
| VPS Contabo Cloud L (Ubuntu 24GB) — đã chốt | $16 | $192 | ~4.9tr |
| Backup add-on (daily snapshot) | $5 | $60 | ~1.5tr |
| Domain `cattuong.ai` | $2 | $25 | ~640k |
| Cloudflare (Proxy DNS + WAF) | $0 | $0 | — |
| Storage (PDF, KB, Qdrant trong VPS) | $0 | $0 | — |
| **CỘNG HẠ TẦNG** | **$23** | **$277** | **~7.1 triệu** |

---

## 3. Phí API Anthropic — 3 mức model

### Bảng giá Claude (cập nhật 2026)

| Model | Input | Output | Tốc độ | Phù hợp |
|---|---|---|---|---|
| **Claude Haiku 4.5** | $1 / 1M token | $5 / 1M token | Nhanh nhất | Chat đơn giản, Q&A nhanh |
| **Claude Sonnet 4.6** | $3 / 1M token | $15 / 1M token | Trung bình | Chat phức tạp, agentic, contract |
| **Claude Opus 4.7** | $15 / 1M token | $75 / 1M token | Chậm | Reasoning sâu, pháp lý phức tạp |

Sonnet **3× đắt hơn** Haiku · Opus **15× đắt hơn** Haiku.

### Tính token mỗi loại call

| Loại request | Input tokens | Output tokens |
|---|---|---|
| Plain chat (skill + KB top-10 + history) | ~12,000 | ~800 |
| Agentic multi-step (3-5 turns) | ~100,000 | ~3,000 |
| Contract extract (Phase 1 hợp đồng) | ~15,000 | ~5,000 |
| Checklist generation (Phase 2) | ~10,000 | ~2,000 |

### Mức sử dụng giả định

**Mixed realistic** (đề xuất khi pilot, sát thực tế):
- 2 light users: 110 chat/tháng (xem báo cáo, hỏi nhanh)
- 3 medium users: 440 chat/tháng + 5% agentic (kế toán viên hằng ngày)
- 1 heavy user: 1,100 chat/tháng + 10% agentic (admin nhập liệu chính)

---

## 4. Chi phí API năm cho 6 users — Mixed realistic

| Model | API / tháng | API / năm | VND / năm |
|---|---|---|---|
| Haiku 4.5 | $62 | $745 | ~19 triệu |
| **Sonnet 4.6** | $187 | $2,244 | ~57 triệu |
| Opus 4.7 | $935 | $11,220 | ~286 triệu |

## 5. Chi phí API năm — Worst case (6 heavy users đầy tải)

| Model | API / tháng | API / năm | VND / năm |
|---|---|---|---|
| Haiku 4.5 | $182 | $2,178 | ~56 triệu |
| Sonnet 4.6 | $544 | $6,534 | ~167 triệu |
| Opus 4.7 | $2,722 | $32,670 | ~833 triệu |

---

## 6. Tổng chi phí HẰNG NĂM (API + Hạ tầng)

### Mixed realistic

| Phương án | API/năm | VPS/năm | **TỔNG/năm USD** | **TỔNG/năm VND** |
|---|---|---|---|---|
| Haiku 4.5 (hiện tại) | $745 | $277 | **$1,022** | **~26 triệu** |
| **Sonnet 4.6** | $2,244 | $277 | **$2,521** | **~64 triệu** |
| Opus 4.7 | $11,220 | $277 | **$11,497** | **~293 triệu** |

### Worst case (6 heavy)

| Phương án | API/năm | VPS/năm | **TỔNG/năm USD** | **TỔNG/năm VND** |
|---|---|---|---|---|
| Haiku 4.5 | $2,178 | $277 | $2,455 | ~63 triệu |
| Sonnet 4.6 | $6,534 | $277 | $6,811 | ~174 triệu |
| Opus 4.7 | $32,670 | $277 | $32,947 | ~840 triệu |

---

## 7. Smart Routing — Tối ưu chi phí 2-4×

Thay vì dùng 1 model cho mọi request, **routing thông minh** theo độ phức tạp:

```
90% requests đơn giản            → Haiku 4.5  (rẻ + nhanh)
8%  requests có RAG dày / KB     → Sonnet 4.6 (chất lượng cao)
2%  requests siêu phức tạp       → Opus 4.7   (reasoning sâu)
```

Kết hợp với **Prompt Caching** (Anthropic native, giảm 90% input cost trên phần cache lặp lại):

| Phương án | API/năm | + VPS | **TỔNG/năm** | VND/năm |
|---|---|---|---|---|
| Sonnet-only (không tối ưu) | $2,244 | $277 | $2,521 | ~64tr |
| Sonnet + Smart routing | $1,800 | $277 | $2,077 | ~53tr |
| **Sonnet + Smart + Caching** ⭐ | $1,080 | $277 | **$1,357** | **~35 triệu** |
| **Haiku + Smart + Caching** ⭐⭐ | $540 | $277 | **$817** | **~21 triệu** |

→ Áp dụng cả Smart routing + Prompt caching: **giảm 38-50% chi phí API**.

---

## 8. So sánh với phần mềm enterprise (cùng quy mô 6 users)

| Giải pháp | Chi phí/năm | Tính năng |
|---|---|---|
| **AIQ + Smart + cache** ⭐ | ~21 triệu | AI chat + KB + agentic + accounting + contract |
| **AIQ + Sonnet-only** | ~66 triệu | Như trên, chất lượng cao đồng đều |
| MISA SME (chỉ kế toán) | 30-60 triệu | Chỉ kế toán, không AI |
| Bravo / Fast Accounting | 50-100 triệu | Kế toán + ERP, không AI |
| Microsoft 365 Copilot | ~55 triệu | Office docs only, không VN domain |
| ChatGPT Enterprise | ~$60/user/mo × 6 × 12 = $4,320 ≈ 110 triệu | General chat, không tích hợp KB riêng |

→ AIQ cung cấp **giá trị 3-5× cao hơn / chi phí 3-5× thấp hơn** so với enterprise alternatives, **có thêm AI**.

---

## 9. Tóm tắt trực quan

```
TỔNG CHI PHÍ HẰNG NĂM cho 6 users (gồm VPS Ubuntu 24GB)

21tr  ████ Haiku + Smart routing + cache  ⭐⭐ rẻ nhất
26tr  █████ Haiku hiện tại
35tr  ██████ Sonnet + smart + cache       ⭐ cân bằng
64tr  ████████████ Sonnet-only
84tr  ███████████████ Sonnet + Opus combo
293tr █████████████████████████████████████████████████ Opus-only ⚠️
```

---

## 10. Khuyến nghị triển khai

### Lộ trình 3 giai đoạn (tăng chất lượng & tối ưu chi phí dần)

#### Giai đoạn 1 — Pilot (Tháng 1-2)

- **Model:** Claude Haiku 4.5 (mặc định hiện tại)
- **Chi phí:** ~26 triệu/năm
- **Mục tiêu:** Đo chất lượng thực tế, thu thập feedback user, hiệu chỉnh skills

#### Giai đoạn 2 — Production (Tháng 3-4)

- **Nâng cấp:** Smart routing (90% Haiku + 8% Sonnet + 2% Opus)
- **Thêm:** Prompt caching cho system prompt + skills
- **Chi phí:** ~21 triệu/năm (rẻ hơn pilot nhờ caching)
- **Mục tiêu:** Chất lượng cao khi cần, chi phí tối ưu

#### Giai đoạn 3 — Scale (Tháng 5+)

- **Mở rộng:** Thêm module Contract Monitoring (Phase 1-3)
- **Chi phí thêm:** ~$2/tháng cho 6 users upload ~30 contracts/tháng
- **Mục tiêu:** Đầy đủ tính năng kế toán + hợp đồng cho team SME

### Quyết định nhanh

| Lựa chọn | Khi nào chọn |
|---|---|
| **A. Giữ Haiku hiện tại** | Pilot, đo chất lượng trước khi đầu tư |
| **B. Đổi sang Sonnet 4.6** ⭐ | Khi user phản hồi cần chất lượng cao + chấp nhận tăng chi phí 2.4× |
| **C. Smart routing + caching** ⭐⭐ | Khi pilot ổn định, muốn tối ưu chi phí mà giữ chất lượng |
| **D. Opus-only** | Không khuyến nghị (overkill cho use case hiện tại) |

---

## 11. Rủi ro chi phí & cách kiểm soát

| Rủi ro | Mức độ | Mitigation |
|---|---|---|
| User dùng quá nhiều (heavy) | Trung bình | Rate limit per user trong Gateway (đã có) |
| Prompt size phình to (context dày) | Trung bình | Cap KB top-K=10, history=20 messages (đã có) |
| Agentic loop chạy max_turns 8 lần | Cao | Giảm max_turns=4 mặc định, cho user opt-in nhiều turns hơn |
| Anthropic tăng giá | Thấp | Multi-provider abstraction layer (chưa có, future work) |
| VPS down → mất data | Cao | Daily backup ($5/tháng) + Btrfs snapshot |

---

## 12. Tổng kết

| Phương án khuyến nghị | Chi phí năm | / user / tháng |
|---|---|---|
| **Khởi đầu (pilot)** — Haiku hiện tại | **~26 triệu VND** | ~390k |
| **Production (đề xuất)** — Sonnet + Smart + Caching ⭐ | **~35 triệu VND** | ~510k |
| **Khi cần chất lượng tối đa** — Sonnet-only | **~64 triệu VND** | ~915k |

**Bottom line:**
- Mức ngân sách dưới **50 triệu VND/năm** đã đủ chạy AIQ chất lượng cao cho 6 users
- Tiết kiệm 3-5× so với phần mềm kế toán truyền thống mà có thêm năng lực AI đầy đủ

---

## Phụ lục — Phương án giảm chi phí thêm

| Kỹ thuật | Mức tiết kiệm | Effort | Khi nào |
|---|---|---|---|
| Prompt caching (Anthropic native) | -50% input cost | 1h | Triển khai ngay |
| Smart routing 3 model | -30% total | 3h | Sau pilot |
| Giảm KB top-K 10→5 | -25% input | 30 phút | Khi RAG đủ chính xác |
| Giảm history limit 20→10 | -20% input | 30 phút | Khi user không cần nhớ lâu |
| Tự host LLM (Ollama Qwen3) | -100% API cost | 2-3 ngày | Khi volume >$500/tháng |
| Cache embedding queries | -10% latency | 2h | Khi traffic tăng |

---

**Người lập báo cáo:** AIQ Engineering Team
**Phiên bản:** v1.0
**Tài liệu liên quan:**
- Plan triển khai: [`plan.md`](plan.md)
- Progress check hợp đồng: [`progress-check-contracts.md`](progress-check-contracts.md)
- Architecture: [`docs/architecture_structure.md`](docs/architecture_structure.md)
- Roadmap: [`docs/roadmap.md`](docs/roadmap.md)
