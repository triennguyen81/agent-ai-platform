"""Generate the Vietnamese progress report .docx for stakeholders.

Run from the repo root:
    python docs/generate_report.py

Output: docs/bao_cao_tien_do_aiq.docx
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


def add_bullets(doc: Document, items: list[str]) -> None:
    for item in items:
        p = doc.add_paragraph(item, style="List Bullet")
        p.paragraph_format.space_after = Pt(2)


def add_status_table(doc: Document, rows: list[tuple[str, str, str]]) -> None:
    table = doc.add_table(rows=1 + len(rows), cols=3)
    table.style = "Light Grid Accent 1"
    hdr = table.rows[0].cells
    for i, h in enumerate(("Hạng mục", "Trạng thái", "Ghi chú")):
        hdr[i].text = h
        for p in hdr[i].paragraphs:
            for run in p.runs:
                run.bold = True
    for idx, (name, status, note) in enumerate(rows, start=1):
        cells = table.rows[idx].cells
        cells[0].text = name
        cells[1].text = status
        cells[2].text = note
        for cell in cells:
            cell.vertical_alignment = WD_ALIGN_VERTICAL.TOP


def build() -> Path:
    doc = Document()

    # Tweak base font.
    style = doc.styles["Normal"]
    style.font.name = "Calibri"
    style.font.size = Pt(11)

    # Title block
    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = title.add_run("BÁO CÁO TIẾN ĐỘ DỰ ÁN")
    run.bold = True
    run.font.size = Pt(20)
    run.font.color.rgb = RGBColor(0x1F, 0x2A, 0x44)

    sub = doc.add_paragraph()
    sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
    sub_run = sub.add_run("AIQ Agentic AI — Trợ lý AI cá nhân hoá theo chuyên gia")
    sub_run.italic = True
    sub_run.font.size = Pt(13)

    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(
        f"Ngày báo cáo: {date.today().strftime('%d/%m/%Y')}   ·   "
        "Sản phẩm live tại cattuong.ai"
    ).italic = True

    doc.add_paragraph()

    # 1. Tóm tắt
    add_heading(doc, "1. Tóm tắt sản phẩm", level=1)
    doc.add_paragraph(
        "AIQ Agentic AI là nền tảng trợ lý AI cá nhân hoá theo chuyên gia. "
        "Mỗi user có một “kho tri thức” riêng (notes, tài liệu Markdown); "
        "khi user chat, AI sẽ trả lời dựa trên kiến thức đó (RAG) và trích "
        "dẫn nguồn tham chiếu. Mục tiêu là biến những chuyên gia (nha khoa, "
        "y tế, pháp lý…) thành dịch vụ AI tự phục vụ cho khách hàng của họ."
    )

    # 2. Kiến trúc tổng quan
    add_heading(doc, "2. Kiến trúc tổng quan", level=1)
    doc.add_paragraph(
        "Hệ thống chạy trên Contabo VPS, mỗi service đóng gói container "
        "Docker, ingress qua Cloudflare Proxied + Nginx Proxy Manager."
    )
    add_bullets(doc, [
        "Frontend: app.cattuong.ai — Next.js 15 + React 19, Tailwind v4.",
        "API Gateway: api.cattuong.ai — ASP.NET Core 9, JWT, Dapper + Postgres.",
        "AI Agent: agent.cattuong.ai — FastAPI Python 3.12, gọi Claude Haiku 4.5.",
        "Storage: Postgres (pgvector ext), Redis (chat memory), Qdrant (vector DB cho M6).",
        "Quan sát: Loki + Grafana + Prometheus + Promtail.",
        "Xác thực: Clerk OAuth invite-only, bridge bằng shared secret sang Gateway.",
    ])

    # 3. Hạng mục đã hoàn thành
    add_heading(doc, "3. Các hạng mục đã hoàn thành", level=1)
    add_status_table(doc, [
        ("Docker stack (10 services)", "✅ Live", "postgres, redis, qdrant, loki, grafana, prom, web, gateway, agent, promtail"),
        ("Gateway JWT auth + register/login", "✅ Live", "JWT TTL 7 ngày, hash bcrypt"),
        ("Claude Haiku 4.5 wiring", "✅ Live", "Model: claude-haiku-4-5-20251001"),
        ("Planner-Executor-Critic orchestrator", "✅ Live", "Kích hoạt khi gọi tool; bypass khi chat thường"),
        ("Notes domain (KB CRUD)", "✅ Live", "Tags, per-user isolation, Postgres TEXT[]"),
        ("Triển khai Cosmos VPS + NPM", "✅ Live", "Cloudflare Proxied DNS, port :443 only"),
        ("Domain cattuong.ai + 3 subdomains", "✅ Live", "app, api, agent"),
        ("Clerk invite-only OAuth", "✅ Live", "Magic link, JWT bridge"),
        ("Sample dental KB (14 notes)", "✅ Live", "Seed script"),
        ("RAG v1: inject notes làm context", "✅ Live", "Cap 60K chars/turn"),
        ("Prompt limit 8K → 200K", "✅ Live", "Cho phép RAG context dài"),
        ("Markdown rendering + thinking block + code blocks", "✅ Live", "react-markdown + remark-gfm, nút Copy"),
        ("Bulk .md upload (kéo nhiều file)", "✅ Live", "Đọc YAML frontmatter, retry per file"),
        ("Save-from-chat (lưu trả lời thành note)", "✅ Live", "Modal pre-fill câu hỏi + trả lời"),
        ("Lịch sử chat (sidebar trái + auto-restore)", "✅ Live", "Mở lại trang tự load tin nhắn cũ"),
        ("Thread namespacing theo user", "✅ Live", "Mỗi user chỉ thấy lịch sử của mình"),
        ("Sửa lỗi title hiển thị KB-prefix", "✅ Live", "knowledge_context tách side-channel"),
        ("Phân quyền 3 cấp (super_admin / admin / user)", "✅ Live", "Migration DB + UI cấp role 1-click"),
        ("Voice input STT tiếng Việt (Web Speech API)", "✅ Live", "vi-VN, 3 surface: chat input, save modal, knowledge note"),
        ("Serif font Vietnamese (Lora)", "✅ Live", "Fix decompose tone-mark trên Linux container"),
        ("Skills system Phase 1 — SKILL.md loader", "✅ Live", "4 skill: dental-expert, vietnamese-style, rag-citation, knowledge-curator"),
        ("Skills system Phase 2 — Claude Agent SDK + /agent/run", "✅ Live", "Node 22 + claude CLI v2.1.181; 4 MCP tools (search_notes, get_note, save_note, fetch_url)"),
        ("Skills system Phase 3 — Agentic UI mode toggle", "✅ Live", "Mode pill, URL detection, collapsible trace"),
        ("Skills system Phase 4 — authoring guide + docs", "✅ Live", "docs/skills_authoring.md cho team viết skill mới"),
    ])

    # 4. Phân quyền
    add_heading(doc, "4. Mô hình phân quyền (3 cấp)", level=1)
    perm_table = doc.add_table(rows=4, cols=3)
    perm_table.style = "Light Grid Accent 1"
    hdr = perm_table.rows[0].cells
    for i, h in enumerate(("Vai trò", "Trách nhiệm", "Quyền truy cập")):
        hdr[i].text = h
        for p in hdr[i].paragraphs:
            for run in p.runs:
                run.bold = True
    rows_data = [
        ("Super admin", "Quản trị tối cao", "Quản lý user + cấp/gỡ role; toàn quyền /chat, /knowledge, /admin/users"),
        ("Admin", "Nhập liệu tri thức", "Thêm/sửa notes, bulk upload .md; chat bình thường"),
        ("User", "Tiêu thụ tri thức", "Chỉ chat + lưu kết quả chat làm note (nếu cho)"),
    ]
    for i, (role, resp, perm) in enumerate(rows_data, start=1):
        cells = perm_table.rows[i].cells
        cells[0].text = role
        cells[1].text = resp
        cells[2].text = perm

    # 5. Hạng mục đang/định triển khai tiếp
    add_heading(doc, "5. Lộ trình tiếp theo", level=1)
    add_bullets(doc, [
        "Accounting persona Mức 1 — 4 SKILL.md kế toán + 8 KB notes mẫu (Hệ thống TK, "
        "biểu thuế VAT/TNCN/TNDN, template định khoản). Plan chi tiết tại docs/Ketoan_plan.md.",
        "M6 — Scalable RAG: embedding + top-K via Qdrant (giúp KB scale qua 200+ notes).",
        "Accounting Mức 2 — domain tools: calculate_vat, calculate_pit, lookup_account, "
        "journal_entry_validator, import_excel_ledger.",
        "URL crawler batch (multiple URLs cùng lúc). Single URL đã work qua /agent/run + skill url-ingest.",
        "M8 — Multi-tenant: workspace cho từng tổ chức (org boundary, billing).",
        "M9 — Specialist personas: chuyên gia domain (legal, med, dental, finance).",
        "M10 — Analytics: top câu hỏi, KB coverage gaps.",
        "M11 — Mobile-friendly + PWA.",
        "CI/CD GitHub Actions thay vì manual SSH deploy.",
        "Skills marketplace UI — /admin/skills browse/enable/version skills.",
    ])

    # 6. KPI / số liệu hiện tại
    add_heading(doc, "6. Số liệu hiện tại", level=1)
    kpis = [
        ("Số feature đã ship", "25 / 34 (74%)"),
        ("Service đang chạy production", "10 / 10 healthy"),
        ("Domain live", "app.cattuong.ai · api.cattuong.ai · agent.cattuong.ai"),
        ("Latency chat plain", "~0.8s (Anthropic Haiku 4.5)"),
        ("Latency agentic /agent/run", "5-30s (multi-step + tool use)"),
        ("Cap RAG context hiện tại", "60K chars / turn (~ 200 notes)"),
        ("Số role tier", "3 (super_admin / admin / user)"),
        ("Số skills active", "5 (4 chat skills + 1 agentic url-ingest)"),
        ("Số MCP tools sẵn sàng", "4 (search_notes, get_note, save_note, fetch_url)"),
    ]
    kpi_table = doc.add_table(rows=len(kpis), cols=2)
    kpi_table.style = "Light Grid Accent 1"
    for i, (k, v) in enumerate(kpis):
        cells = kpi_table.rows[i].cells
        cells[0].text = k
        cells[1].text = v
        for cell in cells:
            for p in cell.paragraphs:
                for run in p.runs:
                    if cell == cells[0]:
                        run.bold = True

    # 7. Rủi ro + nợ kỹ thuật
    add_heading(doc, "7. Rủi ro & nợ kỹ thuật", level=1)
    add_bullets(doc, [
        "Triển khai thủ công qua SSH — rollback chậm, cần CI/CD.",
        "RAG full-context cap 60K chars — sẽ vỡ ở ~500 notes (cần M6 Qdrant).",
        "Web build dùng --legacy-peer-deps do React 19 ecosystem chưa update.",
        "Chat history cũ giữ title lỗi (Redis data, không migrate ngược) — sẽ phai dần.",
        "Legacy roles reviewer/uploader/viewer còn trong DB — không gây hại, giữ để tránh FK vỡ.",
    ])

    # 8. Kết luận
    add_heading(doc, "8. Kết luận", level=1)
    doc.add_paragraph(
        "Hệ thống AIQ Agentic AI đã đạt mốc MVP đầy đủ cho thị trường thử "
        "nghiệm: chat AI có hiểu kiến thức, kho tri thức cá nhân, phân "
        "quyền 3 tầng, lịch sử hội thoại, và đầy đủ pipeline triển khai "
        "production. Sản phẩm sẵn sàng nhận user thật cho pilot. Giai "
        "đoạn tiếp theo (Q3/Q4) tập trung vào khả năng scale của RAG, "
        "đa luồng thu thập tri thức và mở rộng cho mô hình multi-tenant."
    )

    out = Path(__file__).parent / "bao_cao_tien_do_aiq.docx"
    doc.save(out)
    return out


if __name__ == "__main__":
    path = build()
    print(f"Generated: {path}")
