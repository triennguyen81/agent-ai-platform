"""Generate baocao.docx from the cost estimate report.

Run from repo root:
    python docs/generate_baocao.py

Output: baocao.docx (in repo root, matching Baocao.md location)
"""

from __future__ import annotations

from datetime import date
from pathlib import Path

from docx import Document
from docx.enum.table import WD_ALIGN_VERTICAL
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.shared import Pt, RGBColor


def add_heading(doc: Document, text: str, level: int = 1) -> None:
    h = doc.add_heading(text, level=level)
    for run in h.runs:
        run.font.color.rgb = RGBColor(0x1F, 0x2A, 0x44)


def add_table(doc: Document, headers: list[str], rows: list[list[str]]) -> None:
    table = doc.add_table(rows=1 + len(rows), cols=len(headers))
    table.style = "Light Grid Accent 1"
    hdr = table.rows[0].cells
    for i, h in enumerate(headers):
        hdr[i].text = h
        for p in hdr[i].paragraphs:
            for run in p.runs:
                run.bold = True
    for r_idx, row in enumerate(rows, start=1):
        cells = table.rows[r_idx].cells
        for c_idx, val in enumerate(row):
            cells[c_idx].text = str(val)
            cells[c_idx].vertical_alignment = WD_ALIGN_VERTICAL.TOP


def add_bullets(doc: Document, items: list[str]) -> None:
    for item in items:
        p = doc.add_paragraph(item, style="List Bullet")
        p.paragraph_format.space_after = Pt(2)


def build() -> Path:
    doc = Document()
    style = doc.styles["Normal"]
    style.font.name = "Calibri"
    style.font.size = Pt(11)

    # ----- Title -----
    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = title.add_run("BÁO CÁO CHI PHÍ TRIỂN KHAI")
    run.bold = True
    run.font.size = Pt(20)
    run.font.color.rgb = RGBColor(0x1F, 0x2A, 0x44)

    sub = doc.add_paragraph()
    sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
    sub_run = sub.add_run("AIQ Agentic AI — 6 users · Ubuntu Server 24GB RAM")
    sub_run.italic = True
    sub_run.font.size = Pt(13)

    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(
        f"Ngày báo cáo: {date.today().strftime('%d/%m/%Y')}   ·   "
        "Phiên bản v1.0"
    ).italic = True

    doc.add_paragraph()

    # ----- 1. Tóm tắt -----
    add_heading(doc, "1. Tóm tắt điều hành", level=1)
    doc.add_paragraph(
        "Báo cáo ước tính chi phí vận hành hằng năm cho hệ thống AIQ Agentic "
        "AI phục vụ 6 người dùng doanh nghiệp Việt Nam, bao gồm hạ tầng máy "
        "chủ, phí API Anthropic và các chi phí phụ trợ."
    )
    add_table(doc, ["Phương án", "Chi phí / năm", "/ user / tháng", "Khuyến nghị"], [
        ["Tiết kiệm nhất (Haiku + Smart + cache)", "~21 triệu VND", "290k VND", "Pilot"],
        ["Cân bằng (Sonnet + Smart + cache) ⭐", "~35 triệu VND", "485k VND", "Đề xuất"],
        ["Chất lượng cao (Sonnet-only)", "~64 triệu VND", "890k VND", "Khi cần"],
        ["Tối đa (Opus-only)", "~293 triệu VND", "4.1tr VND", "Không khuyến nghị"],
    ])
    doc.add_paragraph(
        "So với phần mềm kế toán enterprise (MISA SME ~30-60tr/năm cho 6 "
        "users, không có AI), AIQ rẻ hơn 3-5 lần với năng lực AI đầy đủ."
    )

    # ----- 2. Hạ tầng -----
    add_heading(doc, "2. Hạ tầng máy chủ — VPS Ubuntu 24GB", level=1)
    add_heading(doc, "2.1. So sánh nhà cung cấp", level=2)
    add_table(doc, ["Provider", "Spec", "$/tháng", "Đặc điểm"], [
        ["Contabo Cloud VPS L (đã chốt) ⭐", "8 vCPU · 24GB · 400GB NVMe · 32TB", "$16", "Best price/perf"],
        ["Contabo Cloud VPS XL", "10 vCPU · 24GB · 600GB NVMe", "$23", "Upgrade khi cần disk lớn"],
        ["Hetzner CCX33", "8 vCPU dedicated · 32GB", "$58", "EU data, dedicated CPU"],
        ["Vultr High Performance", "8 vCPU · 24GB", "$96", "Global PoP"],
        ["DigitalOcean Memory-opt", "4 vCPU · 32GB", "$172", "Quá đắt"],
    ])

    add_heading(doc, "2.2. Chi phí hạ tầng hằng năm", level=2)
    add_table(doc, ["Hạng mục", "$/tháng", "$/năm", "VND/năm"], [
        ["VPS Contabo Cloud L (Ubuntu 24GB) — đã chốt", "$16", "$192", "~4.9 triệu"],
        ["Backup add-on (daily snapshot)", "$5", "$60", "~1.5 triệu"],
        ["Domain cattuong.ai", "$2", "$25", "~640k"],
        ["Cloudflare Proxy + WAF", "$0", "$0", "—"],
        ["Storage (PDF/KB/Qdrant in VPS)", "$0", "$0", "—"],
        ["CỘNG HẠ TẦNG", "$23", "$277", "~7.1 triệu"],
    ])

    # ----- 3. Pricing 3 model -----
    add_heading(doc, "3. Phí API Anthropic — 3 mức model", level=1)
    add_heading(doc, "3.1. Bảng giá (2026)", level=2)
    add_table(doc, ["Model", "Input", "Output", "Tốc độ", "Phù hợp"], [
        ["Claude Haiku 4.5", "$1 / 1M token", "$5 / 1M token", "Nhanh nhất", "Chat đơn giản, Q&A"],
        ["Claude Sonnet 4.6", "$3 / 1M token", "$15 / 1M token", "Trung bình", "Chat phức tạp, agentic"],
        ["Claude Opus 4.7", "$15 / 1M token", "$75 / 1M token", "Chậm nhất", "Reasoning pháp lý sâu"],
    ])
    doc.add_paragraph(
        "Sonnet 3× đắt hơn Haiku · Opus 15× đắt hơn Haiku."
    )

    add_heading(doc, "3.2. Token mỗi loại call", level=2)
    add_table(doc, ["Loại request", "Input tokens", "Output tokens"], [
        ["Plain chat (skill + KB + history)", "~12,000", "~800"],
        ["Agentic multi-step (3-5 turns)", "~100,000", "~3,000"],
        ["Contract extract", "~15,000", "~5,000"],
        ["Checklist generation", "~10,000", "~2,000"],
    ])

    # ----- 4. Chi phí API năm -----
    add_heading(doc, "4. Chi phí API năm cho 6 users", level=1)
    add_heading(doc, "4.1. Mixed realistic (2 light + 3 medium + 1 heavy)", level=2)
    add_table(doc, ["Model", "API / tháng", "API / năm", "VND / năm"], [
        ["Haiku 4.5", "$62", "$745", "~19 triệu"],
        ["Sonnet 4.6", "$187", "$2,244", "~57 triệu"],
        ["Opus 4.7", "$935", "$11,220", "~286 triệu"],
    ])

    add_heading(doc, "4.2. Worst case (6 heavy users đầy tải)", level=2)
    add_table(doc, ["Model", "API / tháng", "API / năm", "VND / năm"], [
        ["Haiku 4.5", "$182", "$2,178", "~56 triệu"],
        ["Sonnet 4.6", "$544", "$6,534", "~167 triệu"],
        ["Opus 4.7", "$2,722", "$32,670", "~833 triệu"],
    ])

    # ----- 5. Tổng API + Hạ tầng -----
    add_heading(doc, "5. Tổng chi phí HẰNG NĂM (API + Hạ tầng)", level=1)
    add_heading(doc, "5.1. Mixed realistic", level=2)
    add_table(doc, ["Phương án", "API/năm", "VPS/năm", "TỔNG USD", "TỔNG VND"], [
        ["Haiku 4.5 (hiện tại)", "$745", "$277", "$1,022", "~26 triệu"],
        ["Sonnet 4.6", "$2,244", "$277", "$2,521", "~64 triệu"],
        ["Opus 4.7", "$11,220", "$277", "$11,497", "~293 triệu"],
    ])

    add_heading(doc, "5.2. Worst case (6 heavy)", level=2)
    add_table(doc, ["Phương án", "API/năm", "VPS/năm", "TỔNG USD", "TỔNG VND"], [
        ["Haiku 4.5", "$2,178", "$277", "$2,455", "~63 triệu"],
        ["Sonnet 4.6", "$6,534", "$277", "$6,811", "~174 triệu"],
        ["Opus 4.7", "$32,670", "$277", "$32,947", "~840 triệu"],
    ])

    # ----- 6. Smart Routing -----
    add_heading(doc, "6. Smart Routing — Tối ưu chi phí 2-4×", level=1)
    doc.add_paragraph(
        "Thay vì dùng 1 model cho mọi request, routing thông minh theo độ "
        "phức tạp:"
    )
    add_bullets(doc, [
        "90% requests đơn giản → Haiku 4.5 (rẻ + nhanh)",
        "8% requests có RAG dày / KB → Sonnet 4.6 (chất lượng cao)",
        "2% requests siêu phức tạp → Opus 4.7 (reasoning sâu)",
    ])
    doc.add_paragraph(
        "Kết hợp Prompt Caching (Anthropic native, giảm 90% input cost trên "
        "phần cache lặp lại):"
    )
    add_table(doc, ["Phương án", "API/năm", "+ VPS", "TỔNG USD", "VND/năm"], [
        ["Sonnet-only (không tối ưu)", "$2,244", "$277", "$2,521", "~64tr"],
        ["Sonnet + Smart routing", "$1,800", "$277", "$2,077", "~53tr"],
        ["Sonnet + Smart + Caching ⭐", "$1,080", "$277", "$1,357", "~35 triệu"],
        ["Haiku + Smart + Caching ⭐⭐", "$540", "$277", "$817", "~21 triệu"],
    ])

    # ----- 7. So sánh enterprise -----
    add_heading(doc, "7. So sánh với phần mềm enterprise (cùng 6 users)", level=1)
    add_table(doc, ["Giải pháp", "Chi phí/năm", "Tính năng"], [
        ["AIQ + Smart + cache ⭐", "~21 triệu", "AI chat + KB + agentic + accounting + contract"],
        ["AIQ + Sonnet-only", "~64 triệu", "Như trên, chất lượng cao đồng đều"],
        ["MISA SME (kế toán only)", "30-60 triệu", "Chỉ kế toán, không AI"],
        ["Bravo / Fast Accounting", "50-100 triệu", "Kế toán + ERP, không AI"],
        ["Microsoft 365 Copilot", "~55 triệu", "Office docs only, không VN domain"],
        ["ChatGPT Enterprise", "~110 triệu", "General chat, không tích hợp KB riêng"],
    ])
    doc.add_paragraph(
        "→ AIQ cung cấp giá trị 3-5× cao hơn / chi phí 3-5× thấp hơn so với "
        "enterprise alternatives, có thêm AI."
    )

    # ----- 8. Lộ trình -----
    add_heading(doc, "8. Lộ trình triển khai 3 giai đoạn", level=1)

    add_heading(doc, "Giai đoạn 1 — Pilot (Tháng 1-2)", level=2)
    add_bullets(doc, [
        "Model: Claude Haiku 4.5 (mặc định hiện tại)",
        "Chi phí: ~26 triệu/năm",
        "Mục tiêu: Đo chất lượng, thu feedback, hiệu chỉnh skills",
    ])

    add_heading(doc, "Giai đoạn 2 — Production (Tháng 3-4)", level=2)
    add_bullets(doc, [
        "Nâng cấp: Smart routing (90% Haiku + 8% Sonnet + 2% Opus)",
        "Thêm: Prompt caching cho system prompt + skills",
        "Chi phí: ~21-35 triệu/năm (tuỳ chất lượng yêu cầu)",
        "Mục tiêu: Chất lượng cao khi cần, chi phí tối ưu",
    ])

    add_heading(doc, "Giai đoạn 3 — Scale (Tháng 5+)", level=2)
    add_bullets(doc, [
        "Mở rộng: Thêm module Contract Monitoring (Phase 1-3)",
        "Chi phí thêm: ~$2/tháng cho ~30 contracts upload/tháng",
        "Mục tiêu: Đầy đủ tính năng kế toán + hợp đồng",
    ])

    # ----- 9. Rủi ro -----
    add_heading(doc, "9. Rủi ro chi phí & cách kiểm soát", level=1)
    add_table(doc, ["Rủi ro", "Mức độ", "Mitigation"], [
        ["User dùng quá nhiều (heavy)", "Trung bình", "Rate limit per user (đã có)"],
        ["Prompt size phình to", "Trung bình", "Cap KB top-K=10, history=20 (đã có)"],
        ["Agentic loop chạy max_turns 8", "Cao", "Giảm default max_turns=4, opt-in khi cần"],
        ["Anthropic tăng giá", "Thấp", "Multi-provider abstraction (future)"],
        ["VPS down → mất data", "Cao", "Daily backup ($5/tháng) + Btrfs snapshot"],
    ])

    # ----- 10. Tổng kết -----
    add_heading(doc, "10. Tổng kết & khuyến nghị", level=1)
    add_table(doc, ["Phương án khuyến nghị", "Chi phí năm", "/ user / tháng"], [
        ["Khởi đầu (pilot) — Haiku hiện tại", "~26 triệu VND", "~360k"],
        ["Production (đề xuất) — Sonnet + Smart + Caching ⭐", "~35 triệu VND", "~485k"],
        ["Khi cần chất lượng tối đa — Sonnet-only", "~64 triệu VND", "~890k"],
    ])
    doc.add_paragraph(
        "Bottom line: Ngân sách dưới 50 triệu VND/năm đã đủ chạy AIQ chất "
        "lượng cao cho 6 users. Tiết kiệm 3-5× so với phần mềm kế toán "
        "truyền thống mà có thêm năng lực AI đầy đủ."
    )

    # ----- Phụ lục -----
    add_heading(doc, "Phụ lục — Phương án giảm chi phí thêm", level=1)
    add_table(doc, ["Kỹ thuật", "Mức tiết kiệm", "Effort", "Khi nào"], [
        ["Prompt caching (Anthropic native)", "-50% input cost", "1h", "Ngay"],
        ["Smart routing 3 model", "-30% total", "3h", "Sau pilot"],
        ["Giảm KB top-K 10→5", "-25% input", "30 phút", "Khi RAG đủ chính xác"],
        ["Giảm history limit 20→10", "-20% input", "30 phút", "Khi user không cần nhớ lâu"],
        ["Tự host LLM (Ollama Qwen3)", "-100% API cost", "2-3 ngày", "Khi volume >$500/tháng"],
        ["Cache embedding queries", "-10% latency", "2h", "Khi traffic tăng"],
    ])

    doc.add_paragraph()
    footer = doc.add_paragraph()
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    fr = footer.add_run("— Hết —")
    fr.italic = True

    out = Path(__file__).resolve().parent.parent / "baocao.docx"
    doc.save(out)
    return out


if __name__ == "__main__":
    path = build()
    print(f"Generated: {path}")
