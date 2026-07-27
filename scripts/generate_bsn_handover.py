"""Generate bsn_handover.docx — handover document for anh Toàn.

Contains: intro, credentials, test scenarios, content overview, feedback
template. Vietnamese, ready-to-send.
"""

from __future__ import annotations

from datetime import date
from pathlib import Path

from docx import Document
from docx.enum.table import WD_ALIGN_VERTICAL
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml.ns import qn
from docx.oxml import OxmlElement
from docx.shared import Cm, Pt, RGBColor


REPO_ROOT = Path(__file__).resolve().parent.parent
OUT_PATH = REPO_ROOT / "bsn_handover.docx"


# ---------- Styling helpers -------------------------------------------------


def shade(cell, hex_color: str) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), hex_color)
    tc_pr.append(shd)


def h(doc: Document, text: str, level: int) -> None:
    p = doc.add_heading(text, level=level)
    for run in p.runs:
        if level == 0:
            run.font.size = Pt(24)
            run.font.color.rgb = RGBColor(0x0F, 0x3D, 0x6E)
        elif level == 1:
            run.font.size = Pt(16)
            run.font.color.rgb = RGBColor(0x1F, 0x5A, 0x94)
        elif level == 2:
            run.font.size = Pt(13)
            run.font.color.rgb = RGBColor(0x2A, 0x2A, 0x2A)


def p(doc: Document, text: str, *, bold: bool = False, italic: bool = False, size: int = 11) -> None:
    para = doc.add_paragraph()
    run = para.add_run(text)
    run.font.size = Pt(size)
    run.bold = bold
    run.italic = italic


def bullet(doc: Document, text: str, *, bold_prefix: str | None = None) -> None:
    para = doc.add_paragraph(style="List Bullet")
    if bold_prefix:
        r1 = para.add_run(bold_prefix)
        r1.bold = True
        para.add_run(" — " + text)
    else:
        para.add_run(text)


def numbered(doc: Document, text: str) -> None:
    para = doc.add_paragraph(style="List Number")
    para.add_run(text)


def table(doc: Document, headers: list[str], rows: list[list[str]], header_bg: str = "0F3D6E") -> None:
    tbl = doc.add_table(rows=1 + len(rows), cols=len(headers))
    tbl.style = "Light Grid Accent 1"
    hdr = tbl.rows[0]
    for i, htext in enumerate(headers):
        cell = hdr.cells[i]
        cell.text = ""
        r = cell.paragraphs[0].add_run(htext)
        r.bold = True
        r.font.size = Pt(10)
        r.font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
        shade(cell, header_bg)
        cell.vertical_alignment = WD_ALIGN_VERTICAL.CENTER
    for ri, row in enumerate(rows, start=1):
        for ci, value in enumerate(row):
            cell = tbl.rows[ri].cells[ci]
            cell.text = ""
            r = cell.paragraphs[0].add_run(str(value))
            r.font.size = Pt(10)
            cell.vertical_alignment = WD_ALIGN_VERTICAL.CENTER


# ---------- Document body ---------------------------------------------------


def build() -> Document:
    doc = Document()
    for section in doc.sections:
        section.left_margin = Cm(2.2)
        section.right_margin = Cm(2.2)
        section.top_margin = Cm(1.8)
        section.bottom_margin = Cm(1.8)

    # Cover
    title = doc.add_paragraph()
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    tr = title.add_run("TRỢ LÝ AI CHO BSN")
    tr.font.size = Pt(28)
    tr.bold = True
    tr.font.color.rgb = RGBColor(0x0F, 0x3D, 0x6E)

    sub = doc.add_paragraph()
    sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
    sr = sub.add_run("Bản demo pilot — Sẵn sàng dùng thử")
    sr.font.size = Pt(15)
    sr.italic = True

    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(f"Gửi: Anh Toàn — BSN Strategic Solutions").italic = True
    meta = doc.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(f"Ngày: {date.today().strftime('%d/%m/%Y')}").italic = True

    doc.add_paragraph()

    # 1. Executive summary
    h(doc, "1. Giới thiệu ngắn", level=1)
    p(doc,
        "AI Assistant cho BSN là trợ lý ảo được cấu hình sẵn với toàn bộ thông tin công ty "
        "BSN Strategic Solutions (dịch vụ, ngành phục vụ, phương pháp làm việc, FAQ). "
        "Nhân viên BSN có thể chat trực tiếp để tra cứu nhanh, hoặc chia sẻ link cho khách "
        "hàng tiềm năng tự tìm hiểu thay vì phải chờ nhân sự trả lời.")
    p(doc, "Ba giá trị chính:", bold=True)
    bullet(doc, "trả lời 24/7 mọi câu hỏi cơ bản về dịch vụ, giá, quy trình.", bold_prefix="Trả lời nhanh")
    bullet(doc, "chỉ trả lời dựa trên tài liệu BSN đã cung cấp — không bịa.", bold_prefix="Đúng thông tin")
    bullet(doc, "admin có thể sửa/thêm nội dung bất kỳ lúc nào (không cần dev).", bold_prefix="Dễ cập nhật")

    # 2. Access
    h(doc, "2. Truy cập hệ thống", level=1)
    p(doc, "URL đăng nhập:", bold=True)
    p(doc, "https://app.cattuong.ai/login", size=12)
    doc.add_paragraph()
    p(doc, "Tài khoản demo (đã tạo sẵn cho BSN):", bold=True)
    table(
        doc,
        ["Email", "Mật khẩu", "Vai trò", "Có thể làm gì"],
        [
            ["admin@bsn.vn", "BSN@2026", "Admin BSN",
             "Sửa/thêm/xóa nội dung, gán quyền cho nhân viên, xem tất cả"],
            ["user1@bsn.vn", "BSN@2026", "Nhân viên BSN",
             "Chỉ chat với trợ lý, không sửa được nội dung"],
            ["user2@bsn.vn", "BSN@2026", "Nhân viên BSN", "Như trên"],
            ["user3@bsn.vn", "BSN@2026", "Nhân viên BSN", "Như trên"],
        ],
    )
    p(doc, "→ Đề xuất bắt đầu bằng tài khoản user1 để trải nghiệm giống nhân viên thường.",
      italic=True, size=10)

    # 3. Kịch bản test cho nhân viên
    h(doc, "3. Kịch bản test — Trải nghiệm nhân viên BSN", level=1)
    p(doc, "Đăng nhập bằng user1@bsn.vn → giao diện Chat hiện ra.", bold=True)
    p(doc, "Thử 10 câu hỏi sau, ghi lại chất lượng trả lời:")
    doc.add_paragraph()
    scenarios = [
        "BSN có văn phòng ở đâu?",
        "Tôi là nhà đầu tư Nhật, muốn mở công ty tại VN, BSN hỗ trợ những gì?",
        "Chi phí dịch vụ kế toán hàng tháng khoảng bao nhiêu?",
        "BSN khác gì so với công ty kế toán bình thường?",
        "Xin work permit cho chuyên gia nước ngoài mất bao lâu và giá bao nhiêu?",
        "BSN có phục vụ ngành F&B không? Có gì đặc biệt?",
        "Startup mới chuẩn bị gọi vốn Series A, BSN hỗ trợ được không?",
        "Cần kiểm toán báo cáo tài chính 1 lần, chi phí và thời gian?",
        "BSN đã phục vụ bao nhiêu doanh nghiệp trong 10 năm qua?",
        "Tôi chưa biết mình cần dịch vụ gì, phải làm sao?",
    ]
    for i, s in enumerate(scenarios, 1):
        numbered(doc, s)

    doc.add_paragraph()
    p(doc, "Chấm điểm mỗi câu theo 3 tiêu chí:", bold=True)
    bullet(doc, "1-5. Có đúng thông tin BSN không?", bold_prefix="Chính xác")
    bullet(doc, "1-5. Có dễ hiểu, đầy đủ, không lan man không?", bold_prefix="Hữu ích")
    bullet(doc, "1-5. Câu văn tiếng Việt tự nhiên, không dịch máy?", bold_prefix="Ngôn ngữ")

    # 4. Kịch bản admin
    h(doc, "4. Kịch bản test — Trải nghiệm admin BSN", level=1)
    p(doc, "Đăng nhập bằng admin@bsn.vn.", bold=True)
    p(doc, "Thử các thao tác sau:")
    doc.add_paragraph()
    numbered(doc, "Vào menu \"Knowledge\" — xem 4 tài liệu đã có sẵn về BSN (Giới thiệu / Dịch vụ / Ngành + Phương pháp / FAQ).")
    numbered(doc, "Click vào 1 tài liệu — sửa nội dung bất kỳ (VD sửa hotline) → Save → hỏi lại nhân viên → agent trả với thông tin mới.")
    numbered(doc, "Vào menu \"Agents\" — click vào \"Trợ lý AI BSN\" — xem cấu hình: lifecycle, tier, tools grant, knowledge notes attached (4 notes).")
    numbered(doc, "Vào menu \"Admin\" → \"Users\" — thấy 4 tài khoản BSN. Click \"Gán agent\" trên user bất kỳ để quản lý ai được dùng agent nào.")
    numbered(doc, "Vào menu \"Reviews\" (Review Queue) — nếu muốn tạo agent mới cho BSN, quy trình phải qua review board trước khi lên production.")

    # 5. Nội dung đã có sẵn
    h(doc, "5. Nội dung đã cấu hình sẵn cho BSN", level=1)
    p(doc, "Dựa trên thông tin từ website bsn.vn, tôi đã nạp 4 tài liệu:")
    table(
        doc,
        ["Tài liệu", "Nội dung"],
        [
            ["Giới thiệu công ty BSN",
             "Tên đầy đủ · địa chỉ 2 văn phòng · hotline · email · năng lực (10+ năm, 200+ DN) · 5 điểm khác biệt · phương pháp 4 bước"],
            ["Danh mục 9 dịch vụ",
             "Thành lập DN · Kế toán-Thuế · Kiểm toán · Tuân thủ · Nhân sự · Tư vấn DN · M&A · Visa · F&B (mỗi mảng có mô tả chi tiết)"],
            ["Ngành phục vụ + Phương pháp",
             "8 ngành (Sản xuất · Nông nghiệp · BĐS · TC-NH · FDI · TM-DV · IT · GD-YT) + quy trình Discovery → Strategy → Execution → Evaluate"],
            ["FAQ",
             "12 câu hỏi thường gặp: dịch vụ, phí, khác kế toán thường, phục vụ FDI/startup, cách đặt lịch, thanh toán..."],
        ],
    )
    p(doc, "→ Nếu BSN muốn thêm/sửa nội dung: admin có thể tự làm qua UI, hoặc gửi tài liệu Word/PDF cho tôi để convert sang notes.",
      italic=True, size=10)

    # 6. Cách BSN áp dụng thực tế
    h(doc, "6. Ứng dụng thực tế cho BSN", level=1)
    p(doc, "Ba use case cụ thể có thể triển khai ngay:")
    doc.add_paragraph()
    h(doc, "A. Chat cho nhân viên tra cứu nhanh", level=2)
    bullet(doc, "Nhân viên mới không nhớ hết dịch vụ + quy trình → hỏi bot thay vì hỏi anh chị đồng nghiệp.")
    bullet(doc, "Sales team chuẩn bị pitch cho khách → hỏi bot \"có case study nào ngành sản xuất không?\" để trả lời tự tin.")
    bullet(doc, "Admin tổng hợp báo cáo → dùng bot search nhanh thông tin dịch vụ.")

    h(doc, "B. Chatbot trên website bsn.vn hoặc Zalo OA", level=2)
    bullet(doc, "Nhúng widget chat vào bsn.vn → khách vào web tự chat, giảm 60-70% câu hỏi vào hotline.")
    bullet(doc, "Kết nối Zalo OA của BSN → khách nhắn Zalo, bot trả tự động 24/7.")
    bullet(doc, "Bot chỉ trả câu cơ bản. Câu phức tạp → tự chuyển sang tư vấn viên (workflow escalation).")

    h(doc, "C. Onboarding khách hàng mới", level=2)
    bullet(doc, "Ký hợp đồng xong → khách được cấp tài khoản riêng → tự tra cứu quy trình, tài liệu.")
    bullet(doc, "Giảm thời gian training + email support cho account manager.")

    # 7. Feedback template
    h(doc, "7. Xin ý kiến feedback từ anh Toàn", level=1)
    p(doc, "Sau khi trải nghiệm 30-60 phút, xin cho phản hồi 5 câu:")
    doc.add_paragraph()
    numbered(doc, "10 câu hỏi mẫu — có bao nhiêu câu trả lời đúng thông tin BSN? (X/10)")
    numbered(doc, "Câu nào trả lời sai hoặc thiếu? — vui lòng liệt kê để tôi bổ sung knowledge.")
    numbered(doc, "Nội dung 4 tài liệu có gì cần sửa/thêm về BSN? (giá dịch vụ, đội ngũ, case study...)")
    numbered(doc, "Giao diện có dễ dùng không? Có chỗ nào rối/khó hiểu?")
    numbered(doc, "BSN có muốn triển khai thử 2-4 tuần với 3-5 nhân viên nội bộ không? Nếu có → gói phí phù hợp?")

    doc.add_paragraph()
    p(doc, "Gửi phản hồi về:", bold=True)
    p(doc, "📧 Email: tuong247@gmail.com")
    p(doc, "☎ SĐT: [số điện thoại của Tuong]")
    p(doc, "💬 Có thể trả lời trực tiếp trong file này rồi gửi lại.", italic=True, size=10)

    # 8. Roadmap
    h(doc, "8. Nếu BSN muốn triển khai thật — Roadmap 4 tuần", level=1)
    table(
        doc,
        ["Tuần", "Việc", "BSN cần chuẩn bị"],
        [
            ["1", "Setup tenant riêng, whitelabel branding BSN, cấu hình quyền theo phòng ban",
             "Logo, màu chủ đạo, cơ cấu phòng ban"],
            ["2", "Nạp toàn bộ tài liệu nội bộ BSN (SOP, mẫu HĐ, bảng giá chi tiết, case study)",
             "10-30 tài liệu Word/PDF nội bộ"],
            ["3", "Onboard 5-10 nhân viên BSN + đào tạo admin 1 buổi",
             "Danh sách người dùng + email"],
            ["4", "Chạy pilot, thu feedback, sửa lỗi, tinh chỉnh câu trả lời",
             "1 người điều phối feedback (30 phút/ngày)"],
        ],
    )

    # Closing
    h(doc, "9. Liên hệ", level=1)
    p(doc, "Trân trọng cảm ơn anh Toàn đã dành thời gian trải nghiệm sản phẩm.", italic=True)
    p(doc, "Mọi câu hỏi kỹ thuật, đề xuất tính năng, hoặc thảo luận thương mại — sẵn sàng trao đổi 1:1.")
    doc.add_paragraph()
    p(doc, "Người triển khai: [Tên đầy đủ của Tuong]", bold=True)
    p(doc, "Vai trò: Founder & CTO — AIQ Agentic AI Platform")
    p(doc, "Email: tuong247@gmail.com")
    p(doc, "SĐT / Zalo: [thêm số]")

    return doc


def main() -> None:
    doc = build()
    doc.save(OUT_PATH)
    print(f"Wrote {OUT_PATH}")


if __name__ == "__main__":
    main()
