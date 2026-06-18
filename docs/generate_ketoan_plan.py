"""Generate ketoan_plan.docx from docs/Ketoan_plan.md content.

Run from the repo root:
    python docs/generate_ketoan_plan.py

Output: docs/ketoan_plan.docx
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


def add_table(doc: Document, headers: list[str], rows: list[list[str]]) -> None:
    table = doc.add_table(rows=1 + len(rows), cols=len(headers))
    table.style = "Light Grid Accent 1"
    hdr_cells = table.rows[0].cells
    for i, h in enumerate(headers):
        hdr_cells[i].text = h
        for p in hdr_cells[i].paragraphs:
            for run in p.runs:
                run.bold = True
    for r_idx, row in enumerate(rows, start=1):
        cells = table.rows[r_idx].cells
        for c_idx, val in enumerate(row):
            cells[c_idx].text = str(val)
            cells[c_idx].vertical_alignment = WD_ALIGN_VERTICAL.TOP


def build() -> Path:
    doc = Document()
    style = doc.styles["Normal"]
    style.font.name = "Calibri"
    style.font.size = Pt(11)

    # Title
    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = title.add_run("KẾ HOẠCH TRIỂN KHAI TRỢ LÝ KẾ TOÁN")
    run.bold = True
    run.font.size = Pt(20)
    run.font.color.rgb = RGBColor(0x1F, 0x2A, 0x44)

    sub = doc.add_paragraph()
    sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
    sub_run = sub.add_run("Trên nền tảng AIQ Agentic AI — cattuong.ai")
    sub_run.italic = True
    sub_run.font.size = Pt(13)

    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(
        f"Ngày: {date.today().strftime('%d/%m/%Y')}   ·   "
        "Đối tượng: chủ doanh nghiệp / kế toán trưởng / IT lead"
    ).italic = True

    doc.add_paragraph()

    # 1. Tóm tắt
    add_heading(doc, "1. Tóm tắt", level=1)
    doc.add_paragraph(
        "AIQ được thiết kế là trợ lý AI cá nhân hoá theo chuyên gia. Mỗi "
        "user có một kho tri thức (KB) riêng. Khi đổi sang use-case kế toán, "
        "không cần rewrite code — chỉ cần:"
    )
    add_bullets(doc, [
        "Viết 4 file skill (SKILL.md) chứa quy ước domain kế toán",
        "Nạp KB bằng tài liệu thật: VAS, TT200/TT133, luật thuế, template định khoản",
        "Tuỳ chọn: thêm domain tools (Agent SDK Phase 2) để tính VAT/TNCN/TNDN và import sổ Excel",
        "Tuỳ chọn: thêm UI chuyên dụng (Mức 3) cho workflow nhập hoá đơn / dashboard deadline",
    ])
    doc.add_paragraph(
        "Cùng một platform có thể serve nhiều chuyên ngành (kế toán + nha "
        "khoa + …) qua cơ chế skills + workspace tách user."
    )

    # 2. Vision
    add_heading(doc, "2. Vision — trợ lý kế toán giúp được gì", level=1)
    add_table(
        doc,
        ["Nhu cầu kế toán", "AIQ trả lời được"],
        [
            ["Mua hàng 110 triệu VAT 10%, định khoản ra sao?",
             "Format chuẩn Nợ TK 156/133 — Có TK 331, trích TT200"],
            ["Lương tháng 11 tổng quỹ 850tr, dự đoán TNCN?",
             "Tính theo biểu lũy tiến, trả về số + breakdown"],
            ["Deadline 01/GTGT quý 4 là khi nào?",
             "Tra Luật + Thông tư, trả ngày + cảnh báo trước 7 ngày"],
            ["So sánh TT200 vs TT133 mục khấu hao",
             "Đối chiếu hai văn bản, trích điều khoản"],
            ["Bảng giá dịch vụ tháng này của team?",
             "Lấy từ Note bạn lưu, format bảng"],
        ],
    )
    doc.add_paragraph(
        "Không thay thế: kế toán viên, kiểm toán, phần mềm kế toán "
        "(MISA/Fast/Bravo). Vai trò là hỗ trợ tra cứu + tính nhanh + định "
        "khoản gợi ý."
    ).italic = True

    # 3. Mức 1
    add_heading(doc, "3. Mức 1 — Skills + KB (2 tiếng)", level=1)
    doc.add_paragraph(
        "Mức triển khai zero-code. Không sửa code platform, chỉ thêm file Markdown."
    )

    add_heading(doc, "3.1 — 4 file SKILL.md cần viết", level=2)
    add_table(
        doc,
        ["Skill", "Vai trò"],
        [
            ["accounting-vn-style (always_on)",
             "Quy ước ngôn ngữ + terminology VAS cho mọi câu trả lời. "
             "Xưng hô kế toán/anh-chị; định dạng số tiền Việt; định khoản 2 dòng."],
            ["journal-entry-formatter",
             "Kích hoạt khi keyword định khoản/hạch toán/Nợ TK/Có TK. "
             "Format bút toán đầy đủ, tách dòng VAT, hỏi lại khi case ambiguous."],
            ["tax-calculation",
             "VAT (0/5/8/10%), TNDN (20/15/10%), TNCN biểu lũy tiến 7 bậc + "
             "giảm trừ gia cảnh. Show breakdown chứ không chỉ số cuối."],
            ["compliance-citation",
             "Format trích dẫn TT/NĐ/Luật đầy đủ số/năm/cơ quan ban hành. "
             "Khối 'Văn bản tham chiếu' cuối câu trả lời."],
        ],
    )

    add_heading(doc, "3.2 — 6-10 file KB notes cần nạp", level=2)
    add_table(
        doc,
        ["#", "Title gợi ý", "Nội dung cốt lõi"],
        [
            ["1", "Hệ thống tài khoản TT200 (DN lớn)", "Bảng đầy đủ TK cấp 1, 2 cho 9 loại"],
            ["2", "Hệ thống tài khoản TT133 (SME)", "Bảng cho doanh nghiệp vừa nhỏ"],
            ["3", "Biểu thuế suất VAT 2026", "0%, 5%, 8% (NQ giảm), 10%"],
            ["4", "Biểu thuế TNCN 2026", "7 bậc + giảm trừ gia cảnh"],
            ["5", "TNDN 2026 — thuế suất + ưu đãi", "20%, 15%, 10%, miễn giảm địa bàn"],
            ["6", "Template định khoản phổ biến", "15-20 case: mua/bán/lương/khấu hao/…"],
            ["7", "Deadline tờ khai 2026", "01/GTGT, 02/GTGT, 03/TNDN, 05/TNCN, BCTC"],
            ["8", "Quy trình kế toán cuối tháng", "Chốt sổ, kiểm kê, lập BCTC"],
            ["9", "Quy định hoá đơn điện tử", "TT78/2021 + cập nhật 2024"],
            ["10", "FAQ kế toán nội bộ", "Câu hỏi team kế toán hỏi nhau"],
        ],
    )

    add_heading(doc, "3.3 — Migration với dental skill hiện tại", level=2)
    add_bullets(doc, [
        "Chiến lược A (Coexist) — khuyến nghị: giữ luôn cả dental + accounting "
        "skills, skill loader chọn theo keyword.",
        "Chiến lược B (Swap): xoá dental-expert/, đơn giản hơn nhưng mất khả năng dental.",
    ])

    add_heading(doc, "3.4 — Effort + Outcome", level=2)
    add_bullets(doc, [
        "Effort: 2-3 tiếng (skills) + 2-3 tiếng (note mẫu) + 1 tiếng deploy",
        "Outcome: chat trả lời đúng format VAS, có citation, biết tính VAT/TNCN cơ bản",
    ])

    # 4. Mức 2
    add_heading(doc, "4. Mức 2 — Domain Tools (1-2 ngày)", level=1)
    doc.add_paragraph(
        "Sau khi Mức 1 ổn, thêm tools chuyên ngành vào endpoint /agent/run "
        "(đã có từ Phase 2 SDK)."
    )

    add_table(
        doc,
        ["Tool", "Input", "Output / use case"],
        [
            ["calculate_vat", "amount, rate (0/5/8/10)",
             "Tách giá chưa VAT + VAT + tổng"],
            ["calculate_pit", "gross_monthly, dependents, bhxh",
             "Breakdown 7 bậc + thuế phải nộp"],
            ["calculate_cit", "revenue, expense, special_zone",
             "Thuế TNDN + ưu đãi áp dụng"],
            ["lookup_account", "TK code (vd '156')",
             "Mô tả + cấp 1/2/3 + TK con"],
            ["check_filing_deadline", "form_code, period",
             "Deadline ngày + cảnh báo trễ"],
            ["journal_entry_validator", "list of Nợ/Có lines",
             "OK/lỗi + giải thích"],
            ["import_excel_ledger", "file URL",
             "Summary lines + auto-tag thành notes"],
        ],
    )

    doc.add_paragraph()
    p = doc.add_paragraph()
    p.add_run("Ví dụ workflow agentic:  ").bold = True
    p.add_run(
        'User gọi POST /agent/run với task: "Đọc file Excel sổ T11, tổng hợp '
        'thành cheatsheet, lưu vào KB". Agent tự fetch_url → import_excel_ledger '
        '→ tổng hợp Markdown → save_note. Trả về thông báo đã lưu Note kèm '
        'số nghiệp vụ.'
    )

    add_bullets(doc, [
        "Effort: 1.5-2 ngày code tools + test, 0.5 ngày tích hợp UI",
        "Outcome: AI thực sự tính, không chỉ tra cứu. Batch hoá đơn / sổ tự động.",
    ])

    # 5. Mức 3
    add_heading(doc, "5. Mức 3 — UI chuyên dụng (1-2 tuần)", level=1)
    doc.add_paragraph(
        "Khi user kế toán đông + cần UX dành riêng, build 4-5 page Next.js:"
    )
    add_table(
        doc,
        ["Trang", "Mô tả"],
        [
            ["/accounting/journal",
             "Bảng định khoản: nhập tay + AI suggest mỗi dòng. Save → ghi sổ trong KB."],
            ["/accounting/invoice",
             "Upload PDF hoá đơn → OCR → auto-điền form định khoản"],
            ["/accounting/deadline",
             "Dashboard countdown các tờ khai sắp đến hạn (push notification)"],
            ["/accounting/reports",
             "Sinh BCTC từ KB: BCKQHĐKD, BCĐKT, LCTT — preview + export PDF"],
            ["/accounting/tax-calc",
             "Calculator UI cho VAT/TNCN/TNDN, gọi tools Mức 2"],
        ],
    )
    doc.add_paragraph()
    p = doc.add_paragraph()
    p.add_run("Multi-tenant nhẹ:  ").bold = True
    p.add_run(
        "khi nhiều doanh nghiệp dùng chung — mỗi DN = 1 workspace, KB + skills "
        "tách biệt, super_admin trong workspace quản role nội bộ. Phần này "
        "nằm trong M8 roadmap chung, chưa làm."
    )
    add_bullets(doc, [
        "Effort: 7-10 ngày (3-4 trang + integration + test)",
        "Outcome: sản phẩm trưởng thành cho thị trường SME kế toán Việt Nam",
    ])

    # 6. Roadmap
    add_heading(doc, "6. Lộ trình đề xuất", level=1)
    add_table(
        doc,
        ["Mốc", "Thời gian", "Sản phẩm"],
        [
            ["M-Accounting-1", "Tuần 1",
             "Mức 1 live — chat kế toán đúng format VAS"],
            ["M-Accounting-2", "Tuần 2",
             "Mức 2 tools live — tính thuế + import sổ tự động"],
            ["M-Accounting-3", "Tuần 4",
             "Mức 3 UI v1 — journal/invoice/deadline pages"],
        ],
    )

    # 7. Rủi ro
    add_heading(doc, "7. Rủi ro & lưu ý", level=1)
    add_table(
        doc,
        ["Rủi ro", "Giảm thiểu"],
        [
            ["Trả lời sai pháp luật → user bị phạt",
             "Luôn citation; disclaimer 'tham khảo, không thay thế kế toán viên'"],
            ["Văn bản pháp luật cập nhật liên tục",
             "Note có ngày hiệu lực; bot nhắc check Note mới hơn"],
            ["User nhập sai input → tính ra số sai",
             "Validator tool (tổng nợ ≠ tổng có thì báo)"],
            ["Data nhạy cảm (lương, MST) khi upload",
             "KB nằm trong VPS riêng, không gửi raw ra OpenAI/Anthropic"],
            ["Resistance từ team kế toán",
             "Position là 'trợ lý' — kế toán vẫn duyệt cuối, AI chỉ nháp"],
        ],
    )

    # 8. Quyết định cần chốt
    add_heading(doc, "8. 3 quyết định cần chốt trước khi triển khai", level=1)
    add_bullets(doc, [
        "DN bạn dùng TT200 (DN lớn) hay TT133 (SME)? — KB sẽ tập trung TK chuẩn nào",
        "Có giữ dental skills song song không? — coexist (A) hay swap (B)",
        "Có cần UI chuyên dụng ngay không? — hay pilot Mức 1+2 trước",
    ])

    # 9. Action items
    add_heading(doc, "9. Action items đầu tiên", level=1)
    items = [
        "User chốt 3 câu hỏi mục 8",
        "Tôi viết 4 file SKILL.md kế toán",
        "Tôi sinh 6-8 file Markdown KB mẫu để bạn review",
        "Bạn review + chỉnh nội dung",
        "Upload qua bulk MD trên /knowledge",
        "Test 10 câu hỏi kế toán cơ bản (định khoản, tính thuế, deadline)",
        "Commit + deploy",
        "Pilot với 1-2 kế toán viên trong tuần",
        "Quyết tiếp Mức 2 / Mức 3 dựa trên feedback",
    ]
    for idx, item in enumerate(items, start=1):
        p = doc.add_paragraph(style="List Number")
        p.add_run(item)

    # 10. References
    add_heading(doc, "10. References", level=1)
    add_bullets(doc, [
        "Thông tư 200/2014/TT-BTC — Chế độ kế toán DN lớn",
        "Thông tư 133/2016/TT-BTC — Chế độ kế toán DN vừa và nhỏ",
        "Luật Thuế TNCN số 04/2007 (sửa đổi)",
        "Luật Thuế GTGT số 13/2008",
        "Luật Thuế TNDN số 14/2008",
        "AIQ platform docs: docs/architecture_structure.md, docs/roadmap.md",
    ])

    out = Path(__file__).parent / "ketoan_plan.docx"
    doc.save(out)
    return out


if __name__ == "__main__":
    path = build()
    print(f"Generated: {path}")
