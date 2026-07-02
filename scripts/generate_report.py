"""Generate report_task_done.docx — implementation status report.

Covers both Contract Monitoring Assistant (Phases 1-3) and Agent Governance
Framework (G1-G4). Outputs to repo root as report_task_done.docx.
"""

from __future__ import annotations

from datetime import date
from pathlib import Path

from docx import Document
from docx.enum.table import WD_ALIGN_VERTICAL
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml.ns import qn
from docx.oxml import OxmlElement
from docx.shared import Pt, RGBColor, Cm


REPO_ROOT = Path(__file__).resolve().parent.parent
OUT_PATH = REPO_ROOT / "report_task_done.docx"

# ---------------- Styling helpers ---------------------------------------------


def _shade_cell(cell, hex_color: str) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), hex_color)
    tc_pr.append(shd)


def add_heading(doc: Document, text: str, level: int) -> None:
    h = doc.add_heading(text, level=level)
    for run in h.runs:
        if level == 0:
            run.font.size = Pt(22)
            run.font.color.rgb = RGBColor(0x1F, 0x3A, 0x5F)
        elif level == 1:
            run.font.size = Pt(16)
            run.font.color.rgb = RGBColor(0x2C, 0x5F, 0x8D)
        elif level == 2:
            run.font.size = Pt(13)
            run.font.color.rgb = RGBColor(0x33, 0x33, 0x33)


def add_para(doc: Document, text: str, *, bold: bool = False, italic: bool = False, size: int = 11) -> None:
    p = doc.add_paragraph()
    run = p.add_run(text)
    run.font.size = Pt(size)
    run.bold = bold
    run.italic = italic


def add_bullet(doc: Document, text: str, *, bold_prefix: str | None = None) -> None:
    p = doc.add_paragraph(style="List Bullet")
    if bold_prefix:
        run = p.add_run(bold_prefix)
        run.bold = True
        p.add_run(" — " + text)
    else:
        p.add_run(text)


def add_table(doc: Document, headers: list[str], rows: list[list[str]], *, header_bg: str = "1F3A5F") -> None:
    table = doc.add_table(rows=1 + len(rows), cols=len(headers))
    table.style = "Light Grid Accent 1"

    hdr = table.rows[0]
    for i, h in enumerate(headers):
        cell = hdr.cells[i]
        cell.text = ""
        run = cell.paragraphs[0].add_run(h)
        run.bold = True
        run.font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
        run.font.size = Pt(10)
        _shade_cell(cell, header_bg)
        cell.vertical_alignment = WD_ALIGN_VERTICAL.CENTER

    for r_idx, row in enumerate(rows, start=1):
        for c_idx, value in enumerate(row):
            cell = table.rows[r_idx].cells[c_idx]
            cell.text = ""
            run = cell.paragraphs[0].add_run(str(value))
            run.font.size = Pt(10)
            cell.vertical_alignment = WD_ALIGN_VERTICAL.CENTER


def page_break(doc: Document) -> None:
    p = doc.add_paragraph()
    run = p.add_run()
    run.add_break(3)  # WD_BREAK.PAGE = 7 not in older versions; 3 = page


# ---------------- Document body -----------------------------------------------


def build_document() -> Document:
    doc = Document()

    # Margins
    for section in doc.sections:
        section.left_margin = Cm(2)
        section.right_margin = Cm(2)
        section.top_margin = Cm(1.8)
        section.bottom_margin = Cm(1.8)

    # --- Cover -----
    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    title_run = title.add_run("BÁO CÁO TÌNH HÌNH TRIỂN KHAI")
    title_run.font.size = Pt(26)
    title_run.bold = True
    title_run.font.color.rgb = RGBColor(0x1F, 0x3A, 0x5F)

    subtitle = doc.add_paragraph()
    subtitle.alignment = WD_ALIGN_PARAGRAPH.CENTER
    sub_run = subtitle.add_run("AIQ Agentic AI Platform")
    sub_run.font.size = Pt(16)
    sub_run.italic = True

    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(f"Cập nhật: {date.today().strftime('%d/%m/%Y')}").italic = True

    doc.add_paragraph()
    doc.add_paragraph()

    # --- Executive summary -----
    add_heading(doc, "1. Tóm tắt điều hành", level=1)
    add_para(
        doc,
        "AIQ đang triển khai 2 module song song: (1) Trợ lý Giám sát Hợp đồng — đáp ứng "
        "yêu cầu đối tác trực tiếp; (2) Agent Governance Framework — nền tảng quản trị "
        "multi-tenant cho platform Agentic AI dùng chung.",
    )
    add_para(doc, "Trạng thái hiện tại:", bold=True)
    add_bullet(doc, "3/3 phases đã deploy production trên cosmos VPS.", bold_prefix="Contract Module")
    add_bullet(doc, "4/6 phases đã deploy (G1-G4). G5+G6 deferred sang giai đoạn 2.", bold_prefix="Governance Framework")
    add_bullet(doc, "13 commits feature trong 4 ngày làm việc.", bold_prefix="Cường độ")
    add_bullet(doc, "Web UI sẵn sàng cho user test end-to-end (browser smoke test).", bold_prefix="Sẵn sàng pilot")

    # --- Module 1: Contract Monitoring -----
    doc.add_paragraph()
    add_heading(doc, "2. Module 1 — Trợ lý Giám sát Hợp đồng", level=1)
    add_para(doc, "Mục tiêu: trích xuất hợp đồng PDF → sinh checklist → nhắc deadline.", italic=True)

    add_table(
        doc,
        headers=["Phase", "Mô tả", "Commit", "Trạng thái"],
        rows=[
            [
                "Phase 1",
                "Foundation: upload PDF → OCR → AI extract fields → DB → list/detail view",
                "e72bf58",
                "✅ Deployed",
            ],
            [
                "Phase 2",
                "AI sinh checklist từ clauses + UI interactive (filter, status, delete)",
                "f0e71b4",
                "✅ Deployed",
            ],
            [
                "Phase 3",
                "In-app notifications + Hangfire cron 07:00 VN scan deadlines",
                "cebf063",
                "✅ Deployed",
            ],
        ],
    )

    add_para(doc, "Tài nguyên đã tạo:", bold=True)
    add_bullet(doc, "3 tables (contracts, contract_clauses, contract_checklist_items, notifications)", bold_prefix="Schema")
    add_bullet(doc, "ContractRepository, ChecklistRepository, NotificationRepository + endpoints + Hangfire job", bold_prefix="Gateway")
    add_bullet(doc, "2 skills (contract-extractor, contract-checklist-builder) + 4 MCP tools", bold_prefix="Agent runtime")
    add_bullet(doc, "/contracts, /contracts/[id], /contracts/upload, /notifications + bell icon header", bold_prefix="Web pages")

    add_para(doc, "Demo data đã seed:", bold=True)
    add_bullet(doc, "1 contract HĐ Demo 480M VND + 5 clauses")
    add_bullet(doc, "5 checklist items mix overdue/due-soon/done")
    add_bullet(doc, "3 notifications chưa đọc đẩy lên bell icon")

    # --- Module 2: Agent Governance -----
    doc.add_paragraph()
    add_heading(doc, "3. Module 2 — Agent Governance Framework", level=1)
    add_para(
        doc,
        "Mục tiêu: nâng AIQ từ single-tenant lên multi-tenant Agentic AI Platform "
        "theo 5 nhóm management objects + 7-layer architecture (yêu cầu đối tác mới).",
        italic=True,
    )

    add_table(
        doc,
        headers=["Phase", "Tên", "Commit", "Trạng thái"],
        rows=[
            ["G1", "Tenants + Agent Identity (backend)", "b9ec42b", "✅ Deployed"],
            ["G1", "Tenant switcher + admin/agents UI", "48ad06b", "✅ Deployed"],
            ["G2", "Tool Registry + Action Tier enforcement", "e6de96f", "✅ Deployed"],
            ["G3", "Review Board + lifecycle state-machine guard", "b7dc6a5 + e05ace0", "✅ Deployed"],
            ["G4", "HITL Gates + Pending Actions queue (MVP)", "e24eff1", "✅ Deployed"],
            ["G5", "Decision Trace + Cost Attribution", "—", "⏳ Phase 2 backlog"],
            ["G6", "Governance Body + Immutable audit export", "—", "⏳ Phase 2 backlog"],
        ],
    )

    add_para(doc, "Decisions chốt với defaults (D1-D8):", bold=True)
    add_bullet(doc, "Scope G1+G2+G3 trước, mở rộng G4 thêm", bold_prefix="D1")
    add_bullet(doc, "Multi-tenant: 1 user thuộc N tenants với role khác nhau", bold_prefix="D2")
    add_bullet(doc, "Agent tách khỏi Skill (Agent = entity quản lý, skill = capability)", bold_prefix="D3")
    add_bullet(doc, "Compliance NĐ 13/2023 MVP, GDPR/SOC2 sau", bold_prefix="D4")
    add_bullet(doc, "Contract Assistant backward-compat (gắn thành agent contract-monitor tenant default)", bold_prefix="D5")
    add_bullet(doc, "Tenant isolation app-level filter (RLS sau pilot)", bold_prefix="D6")
    add_bullet(doc, "Action Tier default = suggest", bold_prefix="D7")
    add_bullet(doc, "OIDC service accounts deferred", bold_prefix="D8")

    # --- Architecture state -----
    doc.add_paragraph()
    add_heading(doc, "4. Hiện trạng kiến trúc 7 lớp", level=1)

    add_table(
        doc,
        headers=["Layer", "AIQ có", "Gap còn lại"],
        rows=[
            ["L7 Governance & Control", "/admin/{tenants,agents,tools,review-queue,hitl-queue,users,skills,audit,health}", "Audit immutable export (G6)"],
            ["L6 Identity & Access", "JWT với tenant_id claim, AgentACL M:N, action_tier enforce", "Per-agent OIDC service accounts"],
            ["L5 Client", "Next.js 15 web + tenant switcher", "Mobile, ERP/CRM connectors (sau)"],
            ["L4 Gateway", "ASP.NET 9 + JWT + RateLimit + Hangfire + TenantContextMiddleware", "Per-agent rate limit enforcement (G4 schema có sẵn)"],
            ["L3 Agent Runtime", "FastAPI + skills + 4 MCP tools + tool registry lookup", "HITL runtime pause/resume + delegation log"],
            ["L2 LLM", "Claude Haiku/Sonnet/Opus + fastembed", "—"],
            ["L1 Data & Infra", "PostgreSQL + Redis + Qdrant với tenant_id mọi bảng", "S3/MinIO cho large file, Kafka event bus (optional)"],
            ["Cross-layer Audit", "audit_log + lifecycle_events + review_votes + pending_actions log", "Decision trace + cost attribution (G5)"],
        ],
    )

    # --- Data backfilled -----
    doc.add_paragraph()
    add_heading(doc, "5. Dữ liệu đã backfill trên production cosmos", level=1)

    add_table(
        doc,
        headers=["Loại", "Giá trị"],
        rows=[
            ["Tenant default", "1 tenant 'AIQ Default Tenant' status=active"],
            ["Tenant members", "4 users đều là tenant_admin (super_admin trong platform)"],
            ["Agent backfill", "contract-monitor lifecycle=production, owner=tuong247, action_tier=execute_with_approval"],
            ["Tool registry", "4 MCP tools seeded (extract/save/get/save_checklist) với risk_tier"],
            ["Tool grants", "4 tools đã grant cho contract-monitor (giữ nguyên hành vi cũ)"],
            ["Review board", "Default Review Board quorum=1, 4 reviewers"],
        ],
    )

    # --- Migrations -----
    doc.add_paragraph()
    add_heading(doc, "6. Migrations đã chạy (15 files SQL)", level=1)

    migrations = [
        ("07_contracts.sql", "Contract Module Phase 1"),
        ("08_contract_checklist.sql", "Contract Module Phase 2"),
        ("09_notifications.sql", "Contract Module Phase 3"),
        ("10_governance_tenants.sql", "Governance G1 - tenants + tenant_members"),
        ("11_governance_agents.sql", "Governance G1 - agents + lifecycle + ACL"),
        ("12_backfill_default_tenant.sql", "Governance G1 - backfill data cũ thành tenant default"),
        ("13_governance_tools.sql", "Governance G2 - tool registry + grants + delegations"),
        ("14_governance_review.sql", "Governance G3 - review boards + requests + votes"),
        ("15_governance_runtime.sql", "Governance G4 - HITL gates + pending actions + rate limits"),
    ]
    for filename, desc in migrations:
        add_bullet(doc, desc, bold_prefix=filename)

    # --- Browser smoke test guide -----
    doc.add_paragraph()
    add_heading(doc, "7. Hướng dẫn smoke test trên browser", level=1)

    add_heading(doc, "7.1. Contract Module", level=2)
    add_bullet(doc, "/contracts → thấy 'HĐ Demo' (đã seed)")
    add_bullet(doc, "Click contract → thấy 5 clauses + 5 checklist items với badges màu")
    add_bullet(doc, "Toggle checkbox / đổi status / delete → state persist")
    add_bullet(doc, "Click 🔔 → /notifications hiện 3 unread")

    add_heading(doc, "7.2. Governance Framework", level=2)
    add_bullet(doc, "/admin/tenants — thấy default tenant")
    add_bullet(doc, "/admin/agents — thấy contract-monitor lifecycle=production")
    add_bullet(doc, "/admin/tools (super_admin) — thấy 4 tools với risk tier")
    add_bullet(doc, "Tạo agent mới → state=draft → transition draft→testing→pending_review")
    add_bullet(doc, "Submit for review → vào /admin/review-queue → Approve → state=production")
    add_bullet(doc, "Tạo HITL gate → Simulate trigger → /admin/hitl-queue → approve/reject")
    add_bullet(doc, "Negative test: thử transition pending_review→production trước approve → 400 lỗi rõ ràng")

    # --- Còn lại -----
    doc.add_paragraph()
    add_heading(doc, "8. Hạng mục còn lại", level=1)

    add_heading(doc, "8.1. Cần user làm", level=2)
    add_bullet(doc, "Smoke test full flow trên browser theo guide ở mục 7")
    add_bullet(doc, "Quyết định scope G5+G6 và timeline pilot 1 tuần")

    add_heading(doc, "8.2. Backlog kỹ thuật", level=2)
    add_bullet(doc, "G4: agent runtime pause/resume khi HITL gate trigger (cần SSE/polling)")
    add_bullet(doc, "G4: per-agent rate limit enforcement (schema có sẵn, cần refactor middleware)")
    add_bullet(doc, "G5: Decision Trace + Cost Attribution (token usage tracking per agent/tenant)")
    add_bullet(doc, "G6: Governance Body + immutable audit export (NĐ 13 compliance)")
    add_bullet(doc, "Notes/Books repos: thêm tenant_id filter (hiện đã backfill nhưng repo chưa filter)")

    # --- Effort summary -----
    doc.add_paragraph()
    add_heading(doc, "9. Effort summary", level=1)

    add_table(
        doc,
        headers=["Hạng mục", "Files modified", "Lines added", "Time"],
        rows=[
            ["Contract Phase 1+2+3", "~30", "+3,000", "~5 ngày"],
            ["Governance G1", "39", "+2,700", "~1 ngày"],
            ["Governance G2", "17", "+1,185", "~0.5 ngày"],
            ["Governance G3", "18", "+1,200", "~1 ngày"],
            ["Governance G4 MVP", "14", "+1,036", "~0.5 ngày"],
            ["TỔNG", "~118", "+9,121", "~8 ngày"],
        ],
    )

    # --- Closing -----
    doc.add_paragraph()
    add_heading(doc, "10. Kết luận", level=1)
    add_para(
        doc,
        "Hệ thống đã sẵn sàng cho pilot test với đối tác. Tất cả module backend + UI core "
        "hoàn thành và deploy lên cosmos. Backlog G5+G6 + runtime pause/resume có thể triển "
        "khai trong giai đoạn 2 sau khi nhận feedback từ pilot.",
    )
    add_para(doc, "Recommendation:", bold=True)
    add_bullet(doc, "Tuần này: user smoke test + nghiệm thu UI flow")
    add_bullet(doc, "Tuần sau: pilot 5 PDF hợp đồng thật với 1 tenant đối tác")
    add_bullet(doc, "2 tuần sau: review feedback → quyết định G5+G6 scope hay tập trung pilot mở rộng")

    return doc


def main() -> None:
    doc = build_document()
    doc.save(OUT_PATH)
    print(f"Wrote {OUT_PATH}")


if __name__ == "__main__":
    main()
