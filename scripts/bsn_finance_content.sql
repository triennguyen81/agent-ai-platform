-- ============================================================================
-- BSN — Financial Strategy Advisory content
--
-- Positioning: "Giải pháp chiến lược tài chính giúp doanh nghiệp tối ưu
-- hiệu quả và tăng trưởng bền vững"
--
-- Target: SME 20-200 người, startup sau vòng gọi vốn đầu, doanh nghiệp
-- gia đình chuyển giao thế hệ.
--
-- Services covered:
--   1. CFO thuê ngoài (Fractional CFO)
--   2. Tư vấn tái cấu trúc tài chính
--   3. Lập kế hoạch tài chính chiến lược 3-5 năm
--   4. FP&A + hệ thống báo cáo quản trị
--   5. Định giá doanh nghiệp (M&A / gọi vốn)
--   6. Tư vấn gọi vốn (Series A-B, PE, ngân hàng)
--   7. Tối ưu thuế chiến lược
--
-- Idempotent — UPDATE existing notes by title, WHERE NOT EXISTS for new ones.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- Overwrite the 4 placeholder notes
-- ---------------------------------------------------------------------------

UPDATE notes SET
    tags = ARRAY['gioi-thieu','cong-ty','tu-van-tai-chinh']::text[],
    content = E'## Về BSN\n' ||
    E'**BSN** là công ty tư vấn chiến lược tài chính hàng đầu Việt Nam, chuyên đồng hành cùng ' ||
    E'doanh nghiệp SME + startup + công ty gia đình để **tối ưu hiệu quả tài chính** và ' ||
    E'**tăng trưởng bền vững**.\n\n' ||
    E'**Chúng tôi không phải công ty kế toán dịch vụ** — chúng tôi là đối tác chiến lược ' ||
    E'giúp lãnh đạo doanh nghiệp ra quyết định tài chính đúng đắn dựa trên dữ liệu.\n\n' ||
    E'### Thông tin liên hệ\n' ||
    E'- **Website:** bsn.vn\n' ||
    E'- **Địa chỉ HN:** Tầng 12, Vincom Center, 191 Bà Triệu, Hai Bà Trưng, Hà Nội\n' ||
    E'- **Địa chỉ HCM:** Tầng 8, Bitexco Financial Tower, Quận 1, TP.HCM\n' ||
    E'- **Hotline:** 1900-BSN-VN (1900-276-86)\n' ||
    E'- **Email tư vấn:** hello@bsn.vn\n' ||
    E'- **MST:** 0316xxxxxx\n\n' ||
    E'### Hồ sơ năng lực\n' ||
    E'- **Thành lập:** 2018\n' ||
    E'- **Đội ngũ:** 32 chuyên gia (18 CPA/ACCA/CFA + 5 MBA + 9 chuyên viên phân tích)\n' ||
    E'- **Đã đồng hành:** 150+ doanh nghiệp, từ startup 5 người đến tập đoàn 2.000 nhân viên\n' ||
    E'- **Ngành phục vụ:** F&B, Bán lẻ, Công nghệ, Sản xuất, Bất động sản, Dịch vụ chuyên môn\n' ||
    E'- **Deals đã hỗ trợ gọi vốn:** > 500 tỷ VNĐ (2023-2025)\n\n' ||
    E'### Sứ mệnh\n' ||
    E'*"Biến số liệu tài chính từ báo cáo cuối kỳ thành công cụ ra quyết định hàng ngày cho lãnh đạo doanh nghiệp Việt."*\n\n' ||
    E'### Giá trị cốt lõi\n' ||
    E'1. **Actionable insights** — không chỉ báo cáo, phải chỉ ra "nên làm gì tiếp"\n' ||
    E'2. **Bảo mật tuyệt đối** — NDA + ISO 27001 + tách data từng khách hàng\n' ||
    E'3. **Đo lường được ROI** — mọi dự án cam kết KPI cụ thể (giảm CP X%, tăng cash Y%...)\n' ||
    E'4. **Cùng làm, không outsourcing** — chuyển giao know-how cho đội nội bộ khách'
WHERE title = 'Giới thiệu công ty BSN';

UPDATE notes SET
    tags = ARRAY['dich-vu','san-pham','bang-gia']::text[],
    content = E'## Danh mục 7 dịch vụ chính\n\n' ||
    E'### 1. CFO thuê ngoài (Fractional CFO)\n' ||
    E'**Vấn đề khách:** DN 30-200 người chưa đủ ngân sách thuê CFO full-time (25-60tr/tháng + phúc lợi) ' ||
    E'nhưng cần tư duy chiến lược tài chính.\n\n' ||
    E'**BSN cung cấp:** 1 CFO senior (10+ năm) dùng chung 2-3 khách, làm việc 2-4 ngày/tháng cho mỗi khách.\n\n' ||
    E'**Bao gồm:**\n' ||
    E'- Review + tư vấn báo cáo tài chính hàng tháng\n' ||
    E'- Xây dựng ngân sách năm + rolling forecast quý\n' ||
    E'- Họp BOD hàng tháng — trình bày P&L, cash flow, KPI\n' ||
    E'- Cố vấn quyết định lớn (đầu tư, tuyển key hire, gọi vốn)\n\n' ||
    E'**Giá:** **35-65 triệu/tháng** tuỳ scope (min cam kết 6 tháng).\n\n' ||
    E'---\n\n' ||
    E'### 2. Tư vấn tái cấu trúc tài chính\n' ||
    E'**Vấn đề khách:** DN đang gặp khó khăn — dòng tiền âm, nợ vay cao, chi phí cồng kềnh, cần "chữa bệnh".\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Chẩn đoán tài chính 2-3 tuần (audit sức khoẻ)\n' ||
    E'- Lập kế hoạch tái cấu trúc 6-18 tháng\n' ||
    E'- Đàm phán tái cơ cấu nợ với NH\n' ||
    E'- Tối ưu chi phí (headcount, vận hành, mua hàng)\n' ||
    E'- Kiểm soát nhân sự thực thi hàng tuần\n\n' ||
    E'**Giá:** **250-800 triệu** trọn gói tuỳ quy mô DN (thường 3-6 tháng chính thức + 3 tháng theo dõi).\n\n' ||
    E'---\n\n' ||
    E'### 3. Lập kế hoạch tài chính chiến lược 3-5 năm\n' ||
    E'**Vấn đề khách:** BOD muốn xác định mục tiêu tăng trưởng, cần biết "cần bao nhiêu vốn, ra sao, khi nào".\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Phân tích thị trường + đối thủ (deep-dive)\n' ||
    E'- Mô hình tài chính 3-5 năm (revenue, cost, cash, capex, working capital)\n' ||
    E'- Kịch bản base/bull/bear + sensitivity analysis\n' ||
    E'- Roadmap hàng năm (mục tiêu, sáng kiến, KPI, PIC)\n\n' ||
    E'**Giá:** **150-400 triệu** trọn gói (4-8 tuần).\n\n' ||
    E'---\n\n' ||
    E'### 4. FP&A + Hệ thống báo cáo quản trị\n' ||
    E'**Vấn đề khách:** BOD chỉ có báo cáo kế toán (BCTC), không có báo cáo quản trị để ra quyết định.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Thiết kế bộ chỉ số quản trị (KPI dashboard) theo ngành\n' ||
    E'- Setup hệ thống báo cáo tự động (Excel + Power BI / Metabase)\n' ||
    E'- Đào tạo đội nội bộ vận hành\n' ||
    E'- Support 3 tháng đầu (chỉnh sửa, tối ưu)\n\n' ||
    E'**Giá:** **80-250 triệu** setup + **15-30 triệu/tháng** duy trì (nếu cần BSN maintain).\n\n' ||
    E'---\n\n' ||
    E'### 5. Định giá doanh nghiệp\n' ||
    E'**Vấn đề khách:** Chuẩn bị M&A, gọi vốn, chuyển nhượng cổ phần, thoả thuận cổ đông, ESOP.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Định giá theo 3 phương pháp: DCF, Comparable, Precedent Transaction\n' ||
    E'- Báo cáo định giá 40-60 trang (Vietnamese/English)\n' ||
    E'- Bảo vệ định giá trong quá trình đàm phán với đối tác/quỹ\n\n' ||
    E'**Giá:** **80-300 triệu** tuỳ độ phức tạp (2-4 tuần).\n\n' ||
    E'---\n\n' ||
    E'### 6. Tư vấn gọi vốn (Series A-B, PE, ngân hàng)\n' ||
    E'**Vấn đề khách:** Cần vốn nhưng không biết bắt đầu từ đâu, không có network, không biết soạn hồ sơ.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Chuẩn bị **Pitch Deck** + **Financial Model** + **Data Room**\n' ||
    E'- Định giá + xây dựng deal structure (equity, convertible note, SAFE)\n' ||
    E'- Kết nối với quỹ VC/PE/gia đình có network\n' ||
    E'- Đồng hành quá trình due diligence + đàm phán\n\n' ||
    E'**Giá:** **300 triệu retainer** + **success fee 2-5% giá trị deal** (min 6 tháng cam kết).\n\n' ||
    E'---\n\n' ||
    E'### 7. Tối ưu thuế chiến lược\n' ||
    E'**Vấn đề khách:** DN đang nộp thuế cao hơn cần thiết vì cấu trúc chưa tối ưu — không phải trốn thuế.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Review cấu trúc pháp lý (holding, subsidiary, R&D unit...)\n' ||
    E'- Tối ưu chi phí được trừ theo TT 78/2014, TT 96/2015\n' ||
    E'- Xin ưu đãi thuế khu công nghệ cao / đầu tư đặc biệt\n' ||
    E'- Chiến lược transfer pricing (nếu DN có công ty liên kết nước ngoài)\n\n' ||
    E'**Giá:** **150-500 triệu** tuỳ quy mô + phức tạp.\n\n' ||
    E'---\n\n' ||
    E'## Combo tối ưu (khách hàng thân thiết)\n' ||
    E'- **Growth Combo** (CFO thuê ngoài + FP&A setup): giảm 15%\n' ||
    E'- **Fundraise Combo** (Định giá + Tư vấn gọi vốn): giảm 20% retainer\n' ||
    E'- **Turnaround Combo** (Tái cấu trúc + CFO 6 tháng): giảm 10%\n\n' ||
    E'## Đăng ký tư vấn\n' ||
    E'📧 **hello@bsn.vn** — phản hồi trong 4 giờ (giờ hành chính)\n' ||
    E'☎ **Hotline:** 1900-276-86 nhánh 1\n' ||
    E'📅 **Đặt lịch discovery call miễn phí 30 phút:** bsn.vn/discovery'
WHERE title = 'Dịch vụ / Sản phẩm chính của BSN';

UPDATE notes SET
    title = 'Quy trình làm việc + cam kết chất lượng BSN',
    tags = ARRAY['quy-trinh','cam-ket','sla']::text[],
    content = E'## Quy trình 5 bước chuẩn BSN\n\n' ||
    E'### Bước 1: Discovery Call (miễn phí, 30 phút)\n' ||
    E'- Khách chia sẻ tình hình + mục tiêu cần đạt\n' ||
    E'- BSN đánh giá sơ bộ + xác định service nào phù hợp\n' ||
    E'- Nếu không phù hợp → BSN từ chối thẳng + giới thiệu đối tác khác (không ép bán)\n\n' ||
    E'### Bước 2: Proposal + báo giá chi tiết (3-5 ngày)\n' ||
    E'- Scope of Work rõ ràng: deliverables + timeline + team\n' ||
    E'- Fee cố định hoặc theo giai đoạn — **KHÔNG hourly billing** để tránh conflict\n' ||
    E'- KPI cam kết cụ thể (VD: "giảm chi phí vận hành 12% trong 6 tháng")\n\n' ||
    E'### Bước 3: Ký hợp đồng + NDA + Kick-off (1 tuần)\n' ||
    E'- HĐ song ngữ nếu khách yêu cầu\n' ||
    E'- NDA 2 chiều (BSN cũng cam kết bảo mật thông tin nội bộ khách)\n' ||
    E'- Kick-off meeting: giới thiệu team, xác nhận scope, thiết lập kênh liên lạc\n\n' ||
    E'### Bước 4: Thực thi + báo cáo hàng tuần (main phase)\n' ||
    E'- **Weekly Standup** 30 phút — cập nhật tiến độ + blocker\n' ||
    E'- **Monthly BOD Review** — trình bày trước BOD khách\n' ||
    E'- **Slack/Zalo group** riêng cho từng dự án — response < 4 giờ trong giờ hành chính\n' ||
    E'- **Data Room** trên Google Drive/OneDrive — versioning + access control\n\n' ||
    E'### Bước 5: Bàn giao + hậu mãi (2-4 tuần cuối)\n' ||
    E'- Final report + presentation cho BOD\n' ||
    E'- Handover đầy đủ cho team nội bộ khách (SOP, template, dashboard)\n' ||
    E'- **Warranty 60 ngày** — hỗ trợ miễn phí clarify/sửa lỗi\n' ||
    E'- Check-in 3 tháng sau — đánh giá kết quả thực tế\n\n' ||
    E'---\n\n' ||
    E'## Cam kết chất lượng (SLA)\n\n' ||
    E'| Cam kết | Chi tiết |\n' ||
    E'|---------|----------|\n' ||
    E'| Response time (giờ HC) | Email: 4h · Chat: 1h · Emergency: 30 phút |\n' ||
    E'| Response time (ngoài giờ) | Email: 12h · Chat: 4h · Emergency: 1h |\n' ||
    E'| Availability team senior | ≥ 95% dự án được lead bởi Senior Manager (5+ năm) |\n' ||
    E'| Bảo mật | ISO 27001 · NDA · Máy tính encrypted · 2FA bắt buộc |\n' ||
    E'| Hoàn tiền | Hoàn 100% nếu không đạt milestone tuần đầu (chưa consume) |\n' ||
    E'| Warranty | 60 ngày sau bàn giao, hỗ trợ miễn phí |\n\n' ||
    E'## Chính sách bảo mật\n\n' ||
    E'- **NDA 2 chiều** bắt buộc trước khi share bất kỳ dữ liệu nào\n' ||
    E'- **ISO 27001 certified** (hệ thống quản lý an toàn thông tin)\n' ||
    E'- **Data separation** — mỗi khách 1 workspace riêng, không share với khách khác\n' ||
    E'- **Truy cập giới hạn** — chỉ team dự án + Managing Partner biết mật khẩu\n' ||
    E'- **Xoá dữ liệu** sau 90 ngày kể từ khi kết thúc dự án (trừ tài liệu pháp lý phải lưu 10 năm)\n' ||
    E'- **Sự cố:** cam kết thông báo khách trong **24 giờ** nếu có breach\n\n' ||
    E'## Chính sách đổi trả / hoàn tiền\n' ||
    E'- **Trước ký HĐ:** discovery call miễn phí, không cam kết gì\n' ||
    E'- **Trong 7 ngày đầu:** hoàn 100% nếu khách đổi ý (chưa consume deliverables)\n' ||
    E'- **Sau tuần đầu:** hoàn theo tỷ lệ phần chưa thực hiện\n' ||
    E'- **KPI không đạt:** BSN hoàn/giảm 20-50% fee theo mức chênh (điều khoản cụ thể trong HĐ)'
WHERE title = 'Chính sách bảo hành & hỗ trợ khách hàng BSN';

UPDATE notes SET
    tags = ARRAY['faq','cau-hoi','khach-hang','tu-van']::text[],
    content = E'## FAQ — Câu hỏi thường gặp\n\n' ||
    E'### 1. BSN khác gì so với công ty kế toán dịch vụ (như Anpha, MISA, Bcom...)?\n' ||
    E'**Rất khác.** Kế toán dịch vụ tập trung vào **compliance** (khai thuế, lập BCTC, kê khai BHXH). ' ||
    E'BSN tập trung vào **strategy** — dùng số liệu tài chính để giúp BOD ra quyết định tăng trưởng.\n\n' ||
    E'Ví dụ:\n' ||
    E'- Kế toán dịch vụ: "Doanh thu tháng 6 là 12 tỷ."\n' ||
    E'- BSN: "Doanh thu 12 tỷ nhưng biên lợi nhuận giảm từ 22% → 17% do CP marketing tăng ' ||
    E'40%. Đề xuất cắt 2 kênh không hiệu quả, tái đầu tư 500tr vào kênh SEO có ROAS 4.2x."\n\n' ||
    E'### 2. Fractional CFO là gì? Có phải giao toàn bộ tài chính cho BSN?\n' ||
    E'**Không.** Fractional CFO nghĩa là **thuê CFO senior part-time** — 2-4 ngày/tháng, chi phí bằng ' ||
    E'~1/3 CFO full-time. CFO của BSN chỉ **cố vấn + review**, không thay thế đội kế toán/tài chính ' ||
    E'nội bộ. Khách vẫn giữ 100% quyết định.\n\n' ||
    E'Phù hợp cho: DN 20-200 người chưa đủ workload cho 1 CFO full-time.\n\n' ||
    E'### 3. Chi phí bao nhiêu cho 1 dự án tư vấn tài chính?\n' ||
    E'Phụ thuộc scope:\n' ||
    E'- **Nhỏ (định giá 1 lần, review nhanh):** 80-300 triệu\n' ||
    E'- **Trung (FP&A setup, kế hoạch 3 năm):** 150-400 triệu\n' ||
    E'- **Lớn (tái cấu trúc toàn diện, gọi vốn):** 500 triệu - 2 tỷ\n' ||
    E'- **Fractional CFO:** 35-65 triệu/tháng, cam kết 6 tháng\n\n' ||
    E'Xem chi tiết trong note "Danh mục dịch vụ".\n\n' ||
    E'### 4. Thời gian trung bình 1 dự án?\n' ||
    E'- **Định giá doanh nghiệp:** 2-4 tuần\n' ||
    E'- **FP&A setup:** 4-8 tuần\n' ||
    E'- **Kế hoạch tài chính 3 năm:** 4-8 tuần\n' ||
    E'- **Tái cấu trúc:** 3-6 tháng chính thức + 3 tháng theo dõi\n' ||
    E'- **Gọi vốn:** 4-9 tháng (từ chuẩn bị đến closing)\n' ||
    E'- **CFO thuê ngoài:** engagement liên tục, min 6 tháng\n\n' ||
    E'### 5. Ai làm việc trực tiếp với khách?\n' ||
    E'Mỗi dự án có team 2-4 người:\n' ||
    E'- **1 Senior Manager / Partner** — lead dự án, đảm bảo chất lượng, gặp BOD hàng tháng\n' ||
    E'- **1-2 Consultant** (mid-level) — thực thi hàng ngày, gặp CFO/kế toán trưởng hàng tuần\n' ||
    E'- **1 Analyst** — làm mô hình + báo cáo (junior)\n\n' ||
    E'Khách được biết tên + CV của toàn team trước khi ký HĐ.\n\n' ||
    E'### 6. Đội ngũ BSN có background gì?\n' ||
    E'- **18/32** có chứng chỉ chuyên môn: CPA, ACCA, CFA, hoặc thạc sĩ tài chính\n' ||
    E'- **Kinh nghiệm trung bình:** 8 năm (Manager) - 15 năm (Partner)\n' ||
    E'- Trước đó từng làm: Big 4 (Deloitte, EY, KPMG, PwC), quỹ đầu tư (Mekong Capital, VinaCapital), ' ||
    E'CFO tập đoàn (FPT, Masan, Vingroup) hoặc startup unicorn (VNG, MoMo)\n\n' ||
    E'### 7. Có ký NDA trước khi trao đổi không?\n' ||
    E'**Có, luôn luôn.** BSN chủ động đề xuất NDA 2 chiều **trước** discovery call nếu khách yêu cầu, ' ||
    E'hoặc trong 24 giờ sau discovery call nếu 2 bên muốn tiếp tục.\n\n' ||
    E'BSN cũng đạt chứng chỉ **ISO 27001** về an toàn thông tin — thông tin khách được ' ||
    E'encrypt tại rest + in transit, chỉ team dự án được access.\n\n' ||
    E'### 8. Có xuất hoá đơn VAT không?\n' ||
    E'**Có, 100%.** BSN xuất hoá đơn điện tử VAT 10% theo NĐ 123/2020, gửi trong 24h sau khi khách ' ||
    E'thanh toán. Bao gồm tất cả thông tin bắt buộc theo TT 78/2021.\n\n' ||
    E'### 9. Thanh toán như thế nào?\n' ||
    E'- **Dự án < 100tr:** thanh toán 100% khi ký HĐ\n' ||
    E'- **Dự án 100-500tr:** 50% khi ký + 50% khi bàn giao\n' ||
    E'- **Dự án > 500tr:** chia 3 milestone (30% ký + 40% giữa + 30% cuối)\n' ||
    E'- **Fractional CFO:** trả trước mỗi tháng vào ngày 5\n' ||
    E'- **Success fee (gọi vốn):** trả sau khi closing deal\n\n' ||
    E'**Phương thức:** chuyển khoản BSN — MB Bank, Vietcombank, Techcombank. ' ||
    E'Không nhận COD, không nhận tiền mặt trên 20tr.\n\n' ||
    E'### 10. BSN có phục vụ startup không?\n' ||
    E'**Có, nhưng có tiêu chí.** BSN tập trung startup:\n' ||
    E'- Đã có doanh thu > 3 tỷ/năm\n' ||
    E'- Đã raise seed hoặc pre-A ≥ 500k USD\n' ||
    E'- Đang chuẩn bị Series A/B trong 6-12 tháng tới\n\n' ||
    E'Nếu startup nhỏ hơn → BSN giới thiệu chương trình mentorship miễn phí ' ||
    E'với các founder khác trong network.\n\n' ||
    E'### 11. Có case study nào tôi xem tham khảo?\n' ||
    E'**Có, nhưng đều anonymized.** Xem note "Case study tiêu biểu" hoặc yêu cầu deck riêng ' ||
    E'sau khi ký NDA. BSN chỉ share tên khách cụ thể khi có permission bằng văn bản.\n\n' ||
    E'### 12. Làm việc từ xa hay onsite?\n' ||
    E'**Hybrid:** \n' ||
    E'- Discovery call, weekly standup: online (Google Meet / Zoom)\n' ||
    E'- Monthly BOD review, kick-off, closing: **onsite** tại văn phòng khách\n' ||
    E'- Deep-dive interview với nhân viên: onsite\n\n' ||
    E'BSN có văn phòng HN + HCM, không tính phí đi lại nội thành 2 TP này. ' ||
    E'Ngoài 2 TP → tính phí đi lại thực tế (vé máy bay economy, khách sạn 4 sao, phụ cấp 500k/ngày/người).'
WHERE title = 'FAQ — Câu hỏi thường gặp về BSN';

-- ---------------------------------------------------------------------------
-- Add 4 more notes: case studies + team + industries + tools
-- ---------------------------------------------------------------------------

INSERT INTO notes (user_id, tenant_id, title, content, tags)
SELECT
    (SELECT id FROM users WHERE email='admin@bsn.vn'),
    (SELECT id FROM tenants WHERE code='bsn'),
    t.title, t.content, t.tags
FROM (VALUES
    (
        'Case study tiêu biểu — 5 dự án đã hoàn thành',
        E'*Tên khách đã ẩn danh theo yêu cầu NDA. Có thể chia sẻ tên cụ thể sau khi ký NDA 2 chiều.*\n\n' ||
        E'---\n\n' ||
        E'## Case 1: Chuỗi F&B 45 điểm bán — Tái cấu trúc + gọi vốn Series B\n' ||
        E'**Ngành:** Cà phê chuỗi (F&B casual dining)\n' ||
        E'**Quy mô:** Doanh thu 480 tỷ/năm, 620 nhân viên\n' ||
        E'**Tình huống:** Tăng trưởng nhanh 2020-2023 nhưng biên lợi nhuận âm 4%, dòng tiền căng.\n\n' ||
        E'**BSN làm gì:**\n' ||
        E'- Chẩn đoán 3 tuần → tìm ra 12 điểm bán lỗ (chiếm 40% lỗ toàn hệ thống)\n' ||
        E'- Đóng 8 điểm bán, chuyển 4 điểm sang mô hình franchise\n' ||
        E'- Tái đàm phán 27 hợp đồng thuê mặt bằng → giảm CP thuê 18%\n' ||
        E'- Xây pipeline P&L theo điểm bán → BOD ra quyết định hàng tuần\n' ||
        E'- Chuẩn bị hồ sơ + kết nối với 3 quỹ PE → close Series B **8 triệu USD** sau 7 tháng\n\n' ||
        E'**Kết quả sau 12 tháng:**\n' ||
        E'- Biên EBITDA: -4% → **+9%**\n' ||
        E'- Dòng tiền hoạt động: âm 2 tỷ/tháng → **dương 4.5 tỷ/tháng**\n' ||
        E'- Định giá tăng từ 12 triệu USD → **35 triệu USD**\n\n' ||
        E'**Fee BSN:** 850 triệu (tái cấu trúc) + 300tr retainer + 3% success fee (240k USD)\n\n' ||
        E'---\n\n' ||
        E'## Case 2: Startup SaaS B2B — CFO thuê ngoài + Series A\n' ||
        E'**Ngành:** SaaS quản lý nhân sự cho SME\n' ||
        E'**Quy mô:** ARR 24 tỷ, 38 nhân viên\n' ||
        E'**Tình huống:** Founder tech background, không có CFO, chuẩn bị Series A nhưng model tài chính yếu.\n\n' ||
        E'**BSN làm gì:**\n' ||
        E'- Fractional CFO 3 ngày/tháng — build finance stack (Xero + Notion dashboard)\n' ||
        E'- Xây lại financial model bottom-up với 3 kịch bản\n' ||
        E'- Định giá + chuẩn bị pitch deck + data room\n' ||
        E'- Kết nối 12 quỹ VC Đông Nam Á, close 2 term sheet trong 4 tháng\n\n' ||
        E'**Kết quả:**\n' ||
        E'- Series A **3.2 triệu USD** từ quỹ VC Singapore, định giá pre-money 15tr USD\n' ||
        E'- Founder giữ 68% cổ phần sau round\n\n' ||
        E'**Fee BSN:** 45tr/tháng × 6 tháng CFO + 300tr retainer + 3% success fee ($96k)\n\n' ||
        E'---\n\n' ||
        E'## Case 3: DN gia đình 32 năm — chuyển giao thế hệ 2\n' ||
        E'**Ngành:** Sản xuất bao bì nhựa\n' ||
        E'**Quy mô:** Doanh thu 190 tỷ, 240 nhân viên, 3 nhà máy\n' ||
        E'**Tình huống:** Cha (58 tuổi) muốn chuyển giao cho con trai (32), nhưng không có báo cáo quản trị ' ||
        E'chuẩn, mọi thứ trong đầu cha.\n\n' ||
        E'**BSN làm gì:**\n' ||
        E'- Interview 45 giờ với cha + team quản lý → document toàn bộ know-how tài chính\n' ||
        E'- Setup hệ thống báo cáo quản trị Power BI + dashboard cho BOD\n' ||
        E'- Định giá 3 phương pháp → xác định value 165 tỷ (làm cơ sở phân chia cổ phần gia đình)\n' ||
        E'- Coach con trai 3 tháng — bàn giao vai trò CEO tài chính\n\n' ||
        E'**Kết quả:** Chuyển giao hoàn tất trong 5 tháng, con trai điều hành độc lập từ tháng 6.\n\n' ||
        E'**Fee BSN:** 620 triệu trọn gói (FP&A setup + định giá + coaching)\n\n' ||
        E'---\n\n' ||
        E'## Case 4: Startup e-commerce đang cháy tiền\n' ||
        E'**Ngành:** Fashion e-commerce\n' ||
        E'**Quy mô:** GMV 8 tỷ/tháng, 62 người, dòng tiền âm 800tr/tháng\n' ||
        E'**Tình huống:** Đã raise 2 triệu USD nhưng chỉ còn runway 4 tháng, chưa tìm được product-market-fit rõ.\n\n' ||
        E'**BSN làm gì:**\n' ||
        E'- Emergency assessment 1 tuần → xác định 2 kịch bản: (a) pivot mô hình, (b) đóng cửa\n' ||
        E'- Advise founder chọn pivot → cắt 40% headcount, thay đổi mô hình từ B2C sang B2B\n' ||
        E'- Đàm phán với investor để convert cash burn → bridge round 500k USD\n\n' ||
        E'**Kết quả:** Runway tăng từ 4 tháng → 14 tháng. Sau 8 tháng B2B model đạt gross margin 42%.\n\n' ||
        E'**Fee BSN:** 180 triệu (emergency assessment + advisory 3 tháng)\n\n' ||
        E'---\n\n' ||
        E'## Case 5: Chuỗi bán lẻ mỹ phẩm — Tối ưu thuế\n' ||
        E'**Ngành:** Mỹ phẩm bán lẻ + phân phối\n' ||
        E'**Quy mô:** Doanh thu 320 tỷ, thuế nộp 2024: 42 tỷ\n' ||
        E'**Tình huống:** Nộp thuế TNDN + VAT rất cao vì cấu trúc pháp lý chưa tối ưu.\n\n' ||
        E'**BSN làm gì:**\n' ||
        E'- Review 3 tháng cấu trúc holding + subsidiary\n' ||
        E'- Tái cấu trúc thành 1 holding + 3 subsidiary (bán lẻ / phân phối / R&D)\n' ||
        E'- Xin ưu đãi cho subsidiary R&D → thuế TNDN từ 20% → 10% trong 15 năm\n' ||
        E'- Tối ưu transfer pricing giữa các subsidiary theo TT 45/2021\n\n' ||
        E'**Kết quả năm 2025:**\n' ||
        E'- Thuế TNDN giảm **6.4 tỷ/năm** (từ 24 tỷ → 17.6 tỷ)\n' ||
        E'- ROI dự án: 8.5x trong năm đầu\n\n' ||
        E'**Fee BSN:** 380 triệu trọn gói (audit + implementation 5 tháng)',
        ARRAY['case-study','du-an','thanh-cong']::text[]
    ),
    (
        'Đội ngũ chuyên gia BSN — background + chuyên môn',
        E'## 32 chuyên gia, 4 mảng chuyên môn\n\n' ||
        E'### Managing Partners (3 người)\n\n' ||
        E'**Ông Trần Minh Đức — Managing Partner (Strategy & Fundraising)**\n' ||
        E'- 18 năm kinh nghiệm: 6 năm PwC → 4 năm Mekong Capital → 3 năm CFO tại một unicorn Đông Nam Á → BSN founder\n' ||
        E'- CFA Charterholder, MBA Wharton\n' ||
        E'- Chuyên: gọi vốn Series A-C, M&A, chiến lược tăng trưởng\n' ||
        E'- Đã lead 25+ deal, tổng giá trị > 200 triệu USD\n\n' ||
        E'**Bà Nguyễn Thu Hằng — Managing Partner (Restructuring & Operations)**\n' ||
        E'- 16 năm kinh nghiệm: EY (Deal Advisory) → Grant Thornton (Restructuring) → BSN co-founder\n' ||
        E'- ACCA, MBA INSEAD\n' ||
        E'- Chuyên: tái cấu trúc, turnaround, tối ưu chi phí vận hành\n' ||
        E'- Đã "cứu" 12+ DN khỏi phá sản\n\n' ||
        E'**Ông Lê Quốc Anh — Managing Partner (Family Business & Succession)**\n' ||
        E'- 20 năm: Big 4 → CFO gia đình tập đoàn F&B → cố vấn Family Office\n' ||
        E'- CPA Việt Nam, thạc sĩ Kinh tế UEH\n' ||
        E'- Chuyên: DN gia đình, chuyển giao thế hệ, wealth management\n\n' ||
        E'---\n\n' ||
        E'### Senior Managers (8 người)\n' ||
        E'8-12 năm kinh nghiệm, đa số Big 4 hoặc CFO tập đoàn.\n' ||
        E'- 5 người ACCA/CPA, 2 người CFA Level 2-3, 1 người MBA\n' ||
        E'- Chuyên môn: FP&A, tax planning, valuation, treasury, IPO readiness\n\n' ||
        E'### Managers (9 người)\n' ||
        E'5-8 năm kinh nghiệm, chủ yếu Big 4 + startup CFO.\n' ||
        E'- 6 người ACCA/CPA Việt Nam\n' ||
        E'- Vai trò: lead dự án trung, mentor consultant\n\n' ||
        E'### Consultants + Analysts (12 người)\n' ||
        E'2-5 năm kinh nghiệm, tốt nghiệp NEU/FTU/RMIT top 5%.\n' ||
        E'- 4 người đang học ACCA/CFA\n' ||
        E'- Vai trò: research, financial modeling, data analysis, prep deliverables\n\n' ||
        E'---\n\n' ||
        E'## Cách BSN đào tạo đội ngũ\n' ||
        E'- **50 giờ/năm training bắt buộc** cho mọi cấp bậc\n' ||
        E'- **Rotation** 6 tháng qua các mảng để có view toàn diện\n' ||
        E'- **Mentorship**: mỗi consultant có 1 Senior Manager làm mentor\n' ||
        E'- **BSN Sponsors:** hỗ trợ 100% học phí ACCA/CFA cho nhân viên đủ điều kiện\n\n' ||
        E'## Cam kết khách\n' ||
        E'- **Team consistency:** không thay người giữa chừng dự án (trừ lý do khách quan)\n' ||
        E'- **Escalation:** khách có SĐT trực tiếp Managing Partner phụ trách dự án — gọi bất kỳ lúc nào',
        ARRAY['doi-ngu','chuyen-gia','partner','background']::text[]
    ),
    (
        'Ngành nghề BSN đã phục vụ + chuyên sâu',
        E'## 6 ngành BSN có expertise sâu\n\n' ||
        E'### 1. F&B (Food & Beverage)\n' ||
        E'- **Đặc thù:** biên lợi nhuận mỏng, tồn kho phức tạp, seasonality mạnh\n' ||
        E'- **Chuyên môn BSN:** unit economics theo cửa hàng, quản lý waste, tối ưu labor cost\n' ||
        E'- **Đã phục vụ:** 22 DN — chuỗi cà phê, chuỗi trà sữa, chuỗi ăn nhanh, nhà hàng cao cấp\n' ||
        E'- **Metrics tiêu biểu tracked:** COGS%, labor%, rent%, RevPAM (revenue per available minute)\n\n' ||
        E'### 2. Bán lẻ + E-commerce\n' ||
        E'- **Đặc thù:** vòng quay tồn kho, unit economics kênh, CAC/LTV, working capital\n' ||
        E'- **Chuyên môn BSN:** dashboard SKU-level, marketing ROAS, omnichannel P&L\n' ||
        E'- **Đã phục vụ:** 28 DN — thời trang, mỹ phẩm, gia dụng, đồ chơi\n' ||
        E'- **Metrics tiêu biểu:** Contribution margin/SKU, DSI, ROAS by channel, LTV/CAC\n\n' ||
        E'### 3. Sản xuất (Manufacturing)\n' ||
        E'- **Đặc thù:** capex cao, working capital lớn, chi phí ẩn (utilities, downtime)\n' ||
        E'- **Chuyên môn BSN:** standard costing, ROI capex, OEE, phân bổ overhead\n' ||
        E'- **Đã phục vụ:** 18 DN — bao bì, nhựa, điện tử, may mặc, thực phẩm chế biến\n' ||
        E'- **Metrics tiêu biểu:** OEE, unit cost variance, capacity utilization, capex ROI\n\n' ||
        E'### 4. Công nghệ (SaaS, IT services)\n' ||
        E'- **Đặc thù:** metrics SaaS đặc biệt (MRR, ARR, churn, NRR), revenue recognition IFRS 15\n' ||
        E'- **Chuyên môn BSN:** SaaS modeling, cohort analysis, expansion revenue, cash conversion\n' ||
        E'- **Đã phục vụ:** 24 DN — SaaS B2B, agency, dev outsourcing, EdTech, FinTech\n' ||
        E'- **Metrics tiêu biểu:** MRR/ARR, gross churn, NRR, CAC payback, magic number\n\n' ||
        E'### 5. Bất động sản\n' ||
        E'- **Đặc thù:** cycle dài, cash flow lumpy, quản lý nhiều pháp nhân dự án\n' ||
        E'- **Chuyên môn BSN:** project P&L, IRR/NPV, financing structure, phân bổ chi phí\n' ||
        E'- **Đã phục vụ:** 12 DN — chủ đầu tư căn hộ, chủ đầu tư đất nền, môi giới\n' ||
        E'- **Metrics tiêu biểu:** IRR by project, sell-through rate, days-to-sell, ROIC\n\n' ||
        E'### 6. Dịch vụ chuyên môn (Consulting, Legal, Marketing)\n' ||
        E'- **Đặc thù:** revenue theo billable hour, quản lý utilization, cash flow thuần thu\n' ||
        E'- **Chuyên môn BSN:** utilization tracking, WIP management, project profitability\n' ||
        E'- **Đã phục vụ:** 15 DN — agency, law firm, tư vấn nhân sự, kiến trúc\n\n' ||
        E'---\n\n' ||
        E'## Ngành BSN chưa/không phục vụ\n' ||
        E'- Ngân hàng, bảo hiểm (cần chuyên gia riêng biệt, BSN không claim expert)\n' ||
        E'- Chứng khoán / crypto (compliance phức tạp riêng)\n' ||
        E'- Chăm sóc sức khỏe / bệnh viện (cần certification y tế)\n\n' ||
        E'Nếu khách thuộc ngành ngoài expertise → BSN giới thiệu đối tác chuyên ngành',
        ARRAY['nganh-nghe','industry','case','chuyen-nganh']::text[]
    ),
    (
        'Công cụ + hệ thống BSN sử dụng',
        E'## Stack công cụ BSN\n\n' ||
        E'### Kế toán + ERP\n' ||
        E'- **MISA, Fast, Bravo** — kế toán tài chính Việt Nam\n' ||
        E'- **Xero, QuickBooks Online** — cho startup + DN nhỏ\n' ||
        E'- **SAP S/4HANA, Oracle NetSuite, Microsoft Dynamics 365** — ERP tập đoàn\n' ||
        E'- **Odoo** — nếu khách muốn open source\n\n' ||
        E'### Financial Modeling + Analysis\n' ||
        E'- **Excel + Google Sheets** (chuẩn ngành, luôn dùng)\n' ||
        E'- **Anaplan, Adaptive Insights** — enterprise FP&A\n' ||
        E'- **Cube, Vareto, Mosaic** — startup FP&A\n\n' ||
        E'### Business Intelligence + Dashboard\n' ||
        E'- **Power BI** (chọn 1 phổ biến nhất VN)\n' ||
        E'- **Tableau** (nếu khách đã dùng)\n' ||
        E'- **Metabase, Superset** — open source, chi phí thấp\n' ||
        E'- **Looker Studio** (Google) — miễn phí, cho dự án nhỏ\n\n' ||
        E'### Data Room + Collaboration\n' ||
        E'- **Google Workspace / Microsoft 365** — file sharing, versioning\n' ||
        E'- **Dropbox Business** — cho khách yêu cầu tối đa bảo mật\n' ||
        E'- **Notion** — knowledge base + project tracking\n' ||
        E'- **Slack / Microsoft Teams** — giao tiếp hàng ngày\n' ||
        E'- **Zoom / Google Meet** — họp online\n\n' ||
        E'### Tài chính chuyên biệt\n' ||
        E'- **Capital IQ, PitchBook** — data thị trường, deal comparable\n' ||
        E'- **Bloomberg Terminal** — cho định giá + benchmark (có access tại HN office)\n' ||
        E'- **DealCloud, Affinity** — CRM cho fundraising deals\n\n' ||
        E'### Bảo mật\n' ||
        E'- **Bitwarden Business** — quản lý mật khẩu team\n' ||
        E'- **1Password Business** — cho khách yêu cầu\n' ||
        E'- **Cloudflare Zero Trust** — VPN nội bộ + access control\n' ||
        E'- **AWS KMS / Azure Key Vault** — encryption keys\n\n' ||
        E'---\n\n' ||
        E'## Ai chọn công cụ?\n' ||
        E'BSN **linh hoạt theo khách:**\n' ||
        E'- Nếu khách đã có ERP/dashboard → BSN adapt, không ép thay\n' ||
        E'- Nếu khách cần build mới → BSN đề xuất tối thiểu 2 lựa chọn với so sánh cost/benefit\n' ||
        E'- Đội BSN được training cross-tool → không phụ thuộc 1 tool cụ thể\n\n' ||
        E'## Chi phí license tool ai chịu?\n' ||
        E'- License khách sử dụng lâu dài → khách trả (BSN tư vấn chọn gói phù hợp)\n' ||
        E'- Tool BSN dùng nội bộ (Anaplan, Bloomberg) → BSN chịu, đã tính trong fee\n' ||
        E'- Setup fee một lần → có thể split 50/50 tùy thoả thuận',
        ARRAY['cong-cu','tool','erp','bi']::text[]
    )
) AS t(title, content, tags)
WHERE NOT EXISTS (
    SELECT 1 FROM notes n2
    WHERE n2.tenant_id = (SELECT id FROM tenants WHERE code='bsn')
      AND n2.title = t.title
);

-- Attach the 4 new notes
INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
SELECT
    (SELECT a.id FROM agents a JOIN tenants t ON t.id=a.tenant_id WHERE t.code='bsn' AND a.code='bsn-assistant'),
    n.id,
    (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM notes n
WHERE n.tenant_id = (SELECT id FROM tenants WHERE code='bsn')
  AND n.title IN (
      'Case study tiêu biểu — 5 dự án đã hoàn thành',
      'Đội ngũ chuyên gia BSN — background + chuyên môn',
      'Ngành nghề BSN đã phục vụ + chuyên sâu',
      'Công cụ + hệ thống BSN sử dụng'
  )
ON CONFLICT DO NOTHING;

-- Also update the note that had its title changed
UPDATE agent_knowledge
SET attached_at = attached_at  -- no-op just to satisfy the trigger if any
WHERE agent_id = (SELECT a.id FROM agents a JOIN tenants t ON t.id=a.tenant_id WHERE t.code='bsn' AND a.code='bsn-assistant');

COMMIT;

-- Verify
\echo ''
\echo '=== BSN notes summary ==='
SELECT title, LENGTH(content) AS chars, array_length(tags, 1) AS tag_count
FROM notes n
JOIN tenants t ON t.id=n.tenant_id
WHERE t.code='bsn'
ORDER BY title;

\echo ''
\echo '=== BSN agent knowledge count ==='
SELECT a.code, COUNT(ak.note_id) AS notes_attached
FROM agents a
JOIN tenants t ON t.id=a.tenant_id
LEFT JOIN agent_knowledge ak ON ak.agent_id=a.id
WHERE t.code='bsn'
GROUP BY a.code;
