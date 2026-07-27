-- ============================================================================
-- BSN Strategic Solutions — REAL content (fetched from bsn.vn)
--
-- Company: Công ty TNHH Giải pháp chiến lược BSN
-- Address: Tầng 14, HM Town, 412 Nguyễn Thị Minh Khai, Phường Bàn Cờ, TP.HCM
-- Chi nhánh: 171-175 Hàm Nghi, Phường Nguyễn Thái Bình, TP.HCM
-- Phone: 028 3535 6644 · Hotline: 0868 960 917 · Email: info@bsn.vn
--
-- Positioning: "Giải pháp chiến lược tài chính giúp doanh nghiệp tối ưu
-- hiệu quả và tăng trưởng bền vững"
--
-- 9 services + 8 industries + 3 core competencies.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Overwrite the 4 placeholder notes
-- ---------------------------------------------------------------------------

UPDATE notes SET
    tags = ARRAY['gioi-thieu','cong-ty','bsn']::text[],
    content = E'## Công ty TNHH Giải pháp chiến lược BSN\n' ||
    E'*(BSN Strategic Solutions Co., Ltd)*\n\n' ||
    E'### Định vị\n' ||
    E'*"Giải pháp chiến lược tài chính giúp doanh nghiệp tối ưu hiệu quả và tăng trưởng bền vững."*\n\n' ||
    E'### Về BSN\n' ||
    E'BSN là công ty tư vấn chiến lược - tài chính - quản trị chuyên nghiệp, đồng hành cùng doanh ' ||
    E'nghiệp Việt Nam và tổ chức nước ngoài (FDI) trong toàn bộ vòng đời kinh doanh: từ **thành lập** ' ||
    E'→ **vận hành** → **tăng trưởng** → **thoái vốn/M&A**.\n\n' ||
    E'Không giống các đơn vị chỉ làm 1 mảng, BSN cung cấp **giải pháp trọn gói one-stop-shop**: ' ||
    E'kế toán, thuế, kiểm toán, nhân sự, tuân thủ, tư vấn chiến lược, M&A, visa & di trú — tất cả ' ||
    E'dưới cùng 1 đầu mối.\n\n' ||
    E'### Thông tin liên hệ\n' ||
    E'**Trụ sở chính:**\n' ||
    E'Tầng 14, Tòa nhà HM Town\n' ||
    E'412 Nguyễn Thị Minh Khai, Phường Bàn Cờ, TP.HCM\n\n' ||
    E'**Chi nhánh:**\n' ||
    E'171-175 Hàm Nghi, Phường Nguyễn Thái Bình, TP.HCM\n\n' ||
    E'**Liên hệ:**\n' ||
    E'- 📞 Tổng đài: **028 3535 6644**\n' ||
    E'- ☎ Hotline: **0868 960 917**\n' ||
    E'- 📧 Email: **info@bsn.vn**\n' ||
    E'- 🌐 Website: **bsn.vn**\n\n' ||
    E'### Số liệu năng lực\n' ||
    E'- **10+ năm** kinh nghiệm trong tư vấn doanh nghiệp\n' ||
    E'- **200+ doanh nghiệp** đã đồng hành trên toàn quốc\n' ||
    E'- Đội ngũ chuyên gia đa lĩnh vực: **Chiến lược · Tài chính & Quản trị · Chuyển đổi số**\n' ||
    E'- Phục vụ khách trong nước lẫn khối FDI (doanh nghiệp nước ngoài)\n\n' ||
    E'### Điểm khác biệt của BSN\n' ||
    E'1. **Data-driven** — phương pháp tiếp cận dựa trên dữ liệu, không dựa cảm tính\n' ||
    E'2. **Đồng hành đầy đủ** — từ tư vấn → triển khai → đánh giá kết quả (không chỉ đưa slide)\n' ||
    E'3. **Cá nhân hóa** — mỗi giải pháp thiết kế phù hợp đặc thù từng doanh nghiệp\n' ||
    E'4. **Kết hợp 3 chiều** — tư duy chiến lược + công nghệ hiện đại + kinh nghiệm thực tế\n' ||
    E'5. **One-stop-shop** — 1 đầu mối cho 9+ mảng dịch vụ, giảm phối hợp nhiều bên\n\n' ||
    E'### Phương pháp tiếp cận chuẩn BSN\n' ||
    E'1. **Xác định tầm nhìn + mục tiêu** phát triển của doanh nghiệp\n' ||
    E'2. **Xây dựng chiến lược** trung và dài hạn (3-5 năm)\n' ||
    E'3. **Lộ trình triển khai** phù hợp năng lực hiện tại của khách\n' ||
    E'4. **Đánh giá + hiệu chỉnh** theo checkpoint định kỳ'
WHERE title = 'Giới thiệu công ty BSN';

UPDATE notes SET
    tags = ARRAY['dich-vu','service','tu-van']::text[],
    content = E'## 9 dịch vụ chính của BSN\n\n' ||
    E'---\n\n' ||
    E'### 1. Thành lập doanh nghiệp & Gia nhập thị trường\n' ||
    E'**Đối tượng:** Startup, doanh nghiệp mới, FDI vào Việt Nam.\n\n' ||
    E'**BSN hỗ trợ:**\n' ||
    E'- Tư vấn cấu trúc pháp lý phù hợp (TNHH, CP, chi nhánh, VPĐD)\n' ||
    E'- Chuẩn bị hồ sơ đăng ký kinh doanh, giấy phép con\n' ||
    E'- Xin cấp GPĐT cho FDI (Investment Registration Certificate)\n' ||
    E'- Tư vấn địa điểm đặt trụ sở, mở tài khoản ngân hàng\n' ||
    E'- Đăng ký con dấu, chữ ký số, hoá đơn điện tử\n' ||
    E'- Setup ban đầu về nhân sự, kế toán, hệ thống\n\n' ||
    E'---\n\n' ||
    E'### 2. Kế toán & Thuế\n' ||
    E'**Đối tượng:** DN vừa và nhỏ chưa đủ ngân sách thuê kế toán trưởng riêng.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Dịch vụ kế toán trọn gói hàng tháng/quý\n' ||
    E'- Lập BCTC theo Chuẩn mực Kế toán VN (VAS) và IFRS (khi cần)\n' ||
    E'- Kê khai thuế: VAT, TNCN, TNDN, môn bài, BHXH\n' ||
    E'- Tư vấn tối ưu thuế trong khung pháp luật\n' ||
    E'- Xử lý hoàn thuế GTGT, quyết toán thuế cuối năm\n' ||
    E'- Đại diện làm việc với cơ quan thuế khi có thanh kiểm tra\n\n' ||
    E'---\n\n' ||
    E'### 3. Kiểm toán & Đảm bảo\n' ||
    E'**Đối tượng:** DN có yêu cầu kiểm toán độc lập (bắt buộc/tự nguyện), chuẩn bị M&A/IPO.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Kiểm toán BCTC theo VSA và ISA\n' ||
    E'- Kiểm toán đầu tư (báo cáo quyết toán dự án)\n' ||
    E'- Đánh giá + kiểm soát nội bộ (Internal Audit)\n' ||
    E'- Due Diligence tài chính cho M&A (buy-side + sell-side)\n' ||
    E'- Kiểm toán tuân thủ (compliance audit)\n\n' ||
    E'---\n\n' ||
    E'### 4. Tuân thủ & Quản trị doanh nghiệp\n' ||
    E'**Đối tượng:** DN muốn chuẩn hoá governance, DN FDI cần đáp ứng compliance đa quốc gia.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Xây dựng khung quản trị doanh nghiệp (Corporate Governance)\n' ||
    E'- Setup chính sách nội bộ (Code of Conduct, Anti-Bribery, Data Privacy)\n' ||
    E'- Tuân thủ NĐ 13/2023/NĐ-CP về bảo vệ dữ liệu cá nhân\n' ||
    E'- Đánh giá + thực thi các nghĩa vụ báo cáo cho cơ quan chức năng\n' ||
    E'- Tư vấn quản lý rủi ro doanh nghiệp (Enterprise Risk Management)\n\n' ||
    E'---\n\n' ||
    E'### 5. Nhân sự & Tiền lương\n' ||
    E'**Đối tượng:** DN muốn outsource payroll, DN chưa có phòng HR chuyên nghiệp.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Dịch vụ payroll trọn gói (tính lương, TNCN, BHXH, BHYT, BHTN)\n' ||
    E'- Đăng ký + báo tăng/giảm BHXH cho nhân viên\n' ||
    E'- Xây dựng chính sách lương thưởng, KPI, phúc lợi\n' ||
    E'- Soạn thảo HĐLĐ, quy chế nội bộ theo BLLĐ 2019\n' ||
    E'- Tư vấn xử lý kỷ luật lao động, chấm dứt HĐLĐ\n' ||
    E'- Tuyển dụng nhân sự cấp trung/cao (Executive Search)\n\n' ||
    E'---\n\n' ||
    E'### 6. Tư vấn doanh nghiệp (Business Consulting)\n' ||
    E'**Đối tượng:** BOD/CEO cần cố vấn chiến lược ngoài (bên trung lập, có expertise).\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Tư vấn chiến lược tăng trưởng 3-5 năm\n' ||
    E'- Tối ưu quy trình vận hành (Business Process Optimization)\n' ||
    E'- Chuyển đổi số (Digital Transformation): ERP, CRM, BI\n' ||
    E'- Xây dựng KPI + hệ thống báo cáo quản trị\n' ||
    E'- Cải tổ tổ chức, tái cấu trúc phòng ban\n' ||
    E'- Coaching lãnh đạo (CEO/CFO/COO coaching)\n\n' ||
    E'---\n\n' ||
    E'### 7. M&A – Tư vấn giao dịch\n' ||
    E'**Đối tượng:** DN muốn mua lại / bán / gọi vốn / sáp nhập.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- **Buy-side advisory:** target screening, valuation, due diligence, negotiation\n' ||
    E'- **Sell-side advisory:** teaser, IM, data room, buyer outreach, closing\n' ||
    E'- **Fundraising:** hỗ trợ gọi vốn Series A-C từ VC/PE\n' ||
    E'- **Corporate restructuring:** tái cấu trúc cổ đông, ESOP\n' ||
    E'- **Post-merger integration:** đồng hành sau M&A\n\n' ||
    E'---\n\n' ||
    E'### 8. Visa & Di trú lao động\n' ||
    E'**Đối tượng:** Doanh nghiệp có chuyên gia nước ngoài, người nước ngoài đầu tư vào VN.\n\n' ||
    E'**BSN cung cấp:**\n' ||
    E'- Xin **Work Permit** (giấy phép lao động) cho chuyên gia nước ngoài\n' ||
    E'- Xin **Visa DN, LĐ, TT** (thẻ tạm trú 1-2-3-5 năm)\n' ||
    E'- Gia hạn visa, thẻ tạm trú, thẻ thường trú\n' ||
    E'- Bảo lãnh gia đình chuyên gia nước ngoài\n' ||
    E'- Tư vấn thủ tục nhập tịch Việt Nam\n' ||
    E'- Xử lý các vấn đề pháp lý về di trú\n\n' ||
    E'---\n\n' ||
    E'### 9. Giải pháp chuyên sâu ngành F&B\n' ||
    E'**Đối tượng:** Chuỗi nhà hàng, quán cà phê, bakery, delivery kitchen.\n\n' ||
    E'**BSN cung cấp (đặc thù F&B):**\n' ||
    E'- Tư vấn mô hình kinh doanh (dine-in, takeaway, cloud kitchen, franchise)\n' ||
    E'- Xin giấy phép kinh doanh dịch vụ ăn uống + ATVSTP\n' ||
    E'- Quản lý chi phí F&B (food cost, labor cost, prime cost)\n' ||
    E'- Unit economics theo cửa hàng, quản lý inventory\n' ||
    E'- Franchise: xây dựng hệ thống, hợp đồng nhượng quyền\n\n' ||
    E'---\n\n' ||
    E'## 3 lĩnh vực chuyên môn cốt lõi\n' ||
    E'Xuyên suốt 9 dịch vụ trên, BSN tập trung 3 competency:\n' ||
    E'- **Chiến lược** (Strategy) — vision, positioning, growth roadmap\n' ||
    E'- **Tài chính & Quản trị** (Finance & Governance) — CFO service, compliance, controls\n' ||
    E'- **Chuyển đổi số** (Digital Transformation) — ERP, automation, data analytics\n\n' ||
    E'## Đăng ký tư vấn\n' ||
    E'- Hotline: **0868 960 917** (giờ hành chính 8:00-17:30)\n' ||
    E'- Email: **info@bsn.vn** (phản hồi trong 24h)\n' ||
    E'- Đặt lịch tư vấn miễn phí tại: bsn.vn'
WHERE title = 'Dịch vụ / Sản phẩm chính của BSN';

UPDATE notes SET
    title = 'Ngành phục vụ + phương pháp làm việc BSN',
    tags = ARRAY['nganh-nghe','industry','phuong-phap','sla']::text[],
    content = E'## 8 ngành BSN có kinh nghiệm phục vụ\n\n' ||
    E'### 1. Sản xuất (Manufacturing)\n' ||
    E'- Cost accounting, quản lý tồn kho, ROI capex\n' ||
    E'- Compliance môi trường + an toàn lao động\n' ||
    E'- Chuyển đổi số nhà máy (MES, ERP)\n\n' ||
    E'### 2. Nông nghiệp (Agriculture)\n' ||
    E'- Kế toán trang trại, quản lý mùa vụ\n' ||
    E'- Ưu đãi thuế cho nông nghiệp công nghệ cao\n' ||
    E'- Xuất khẩu, chứng nhận GlobalGAP/VietGAP\n\n' ||
    E'### 3. Bất động sản (Real Estate)\n' ||
    E'- Kế toán dự án, phân bổ chi phí phát triển\n' ||
    E'- Compliance NĐ 02/2022 về kinh doanh BĐS\n' ||
    E'- Tư vấn cấu trúc SPV cho từng dự án\n\n' ||
    E'### 4. Tài chính - Ngân hàng\n' ||
    E'- Compliance NHNN, báo cáo tuân thủ Basel\n' ||
    E'- IFRS 9 cho các định chế tài chính\n' ||
    E'- KYC/AML (chống rửa tiền)\n\n' ||
    E'### 5. FDI - Doanh nghiệp nước ngoài\n' ||
    E'- Thành lập công ty vốn nước ngoài\n' ||
    E'- Compliance Luật Đầu tư 2020, chuyển lợi nhuận về nước\n' ||
    E'- Transfer pricing theo TT 45/2021\n' ||
    E'- Visa + work permit cho expatriate\n\n' ||
    E'### 6. Thương mại - Dịch vụ\n' ||
    E'- Kế toán bán lẻ, e-commerce, chuỗi cửa hàng\n' ||
    E'- Quản lý omnichannel (POS + online)\n' ||
    E'- Chuyển đổi số bán hàng\n\n' ||
    E'### 7. Công nghệ thông tin\n' ||
    E'- Kế toán SaaS (revenue recognition IFRS 15)\n' ||
    E'- Ưu đãi thuế phần mềm, khu công nghệ cao\n' ||
    E'- Cấu trúc ESOP cho startup\n\n' ||
    E'### 8. Giáo dục - Y tế\n' ||
    E'- Compliance quy định GD (Bộ GDĐT) / Y tế (Bộ Y tế)\n' ||
    E'- Ưu đãi thuế cho trường học, bệnh viện tư\n' ||
    E'- Kế toán không lợi nhuận (nếu áp dụng)\n\n' ||
    E'---\n\n' ||
    E'## Phương pháp làm việc chuẩn BSN — 4 bước\n\n' ||
    E'### Bước 1: Xác định tầm nhìn + mục tiêu (Discovery)\n' ||
    E'- Discovery call miễn phí 30-60 phút với BOD/CEO khách\n' ||
    E'- Hiểu tình hình hiện tại, mục tiêu 1-3-5 năm, các rào cản\n' ||
    E'- BSN đánh giá match giữa nhu cầu khách và expertise BSN\n\n' ||
    E'### Bước 2: Xây dựng chiến lược trung-dài hạn (Strategy)\n' ||
    E'- Analysis: thị trường, đối thủ, năng lực nội tại\n' ||
    E'- Xác định các sáng kiến chiến lược ưu tiên\n' ||
    E'- Proposal chi tiết + báo giá minh bạch\n\n' ||
    E'### Bước 3: Lộ trình triển khai (Execution)\n' ||
    E'- Chia thành milestones rõ ràng theo tháng/quý\n' ||
    E'- Team BSN + khách cùng thực thi (co-working)\n' ||
    E'- Báo cáo tiến độ hàng tuần, họp BOD hàng tháng\n\n' ||
    E'### Bước 4: Đánh giá + hiệu chỉnh (Evaluate)\n' ||
    E'- KPI cụ thể được đo hàng tháng\n' ||
    E'- Retrospective mỗi quý — điều chỉnh chiến lược nếu cần\n' ||
    E'- Warranty + support sau kết thúc dự án\n\n' ||
    E'---\n\n' ||
    E'## Cam kết chất lượng\n' ||
    E'- Đội ngũ senior (10+ năm) trực tiếp lead mọi dự án\n' ||
    E'- NDA 2 chiều trước khi share dữ liệu nhạy cảm\n' ||
    E'- Response time email < 4h giờ hành chính\n' ||
    E'- Hoàn tiền theo tỷ lệ nếu không đạt KPI thoả thuận'
WHERE title = 'Chính sách bảo hành & hỗ trợ khách hàng BSN';

UPDATE notes SET
    tags = ARRAY['faq','cau-hoi','khach-hang']::text[],
    content = E'## FAQ — Câu hỏi thường gặp về BSN\n\n' ||
    E'### 1. BSN cung cấp những dịch vụ gì?\n' ||
    E'BSN có **9 mảng dịch vụ chính**:\n' ||
    E'1. Thành lập doanh nghiệp + gia nhập thị trường (đặc biệt cho FDI)\n' ||
    E'2. Kế toán & thuế\n' ||
    E'3. Kiểm toán & đảm bảo\n' ||
    E'4. Tuân thủ & quản trị doanh nghiệp\n' ||
    E'5. Nhân sự & tiền lương\n' ||
    E'6. Tư vấn doanh nghiệp (chiến lược, vận hành, chuyển đổi số)\n' ||
    E'7. M&A - tư vấn giao dịch\n' ||
    E'8. Visa & di trú lao động\n' ||
    E'9. Chuyên sâu ngành F&B\n\n' ||
    E'Xem chi tiết từng dịch vụ ở note "Danh mục dịch vụ".\n\n' ||
    E'### 2. BSN có phục vụ doanh nghiệp nước ngoài (FDI) không?\n' ||
    E'**Có, đây là 1 trong 3 thế mạnh của BSN.** BSN hỗ trợ FDI trọn gói từ:\n' ||
    E'- Xin GPĐT (Investment Registration Certificate)\n' ||
    E'- Thành lập pháp nhân (TNHH, CP, chi nhánh, VPĐD)\n' ||
    E'- Compliance Luật Đầu tư 2020\n' ||
    E'- Transfer pricing, chuyển lợi nhuận về nước\n' ||
    E'- Visa + work permit cho expatriate\n' ||
    E'- Kế toán theo IFRS song song VAS\n\n' ||
    E'### 3. BSN khác gì so với công ty kế toán dịch vụ khác?\n' ||
    E'| Tiêu chí | Kế toán dịch vụ thông thường | BSN |\n' ||
    E'|----------|------------------------------|-----|\n' ||
    E'| Phạm vi | Kê khai thuế + BCTC | 9+ mảng one-stop-shop |\n' ||
    E'| Tính chiến lược | Compliance-focused | Data-driven + strategic |\n' ||
    E'| Đội ngũ | Kế toán viên | Chuyên gia đa lĩnh vực (kiểm toán, tài chính, luật, HR, IT) |\n' ||
    E'| Cách tiếp cận | Xử lý transaction | Đồng hành từ tư vấn → triển khai → đánh giá |\n' ||
    E'| Khách hàng | SME phổ thông | SME + FDI + M&A + tổ chức lớn |\n\n' ||
    E'### 4. BSN có bao nhiêu năm kinh nghiệm và đã phục vụ bao nhiêu khách?\n' ||
    E'- **10+ năm** kinh nghiệm hoạt động\n' ||
    E'- **200+ doanh nghiệp** đã đồng hành trên nhiều lĩnh vực\n' ||
    E'- Đội ngũ chuyên gia đa lĩnh vực với chứng chỉ ACCA, CPA, CFA, luật sư\n\n' ||
    E'### 5. BSN có phục vụ startup không?\n' ||
    E'**Có.** BSN đặc biệt phù hợp cho startup ở các giai đoạn:\n' ||
    E'- **Founding stage:** thành lập DN, setup pháp lý + kế toán ban đầu\n' ||
    E'- **Growth stage:** tư vấn chiến lược, gọi vốn Series A-B\n' ||
    E'- **Scale stage:** M&A, mở rộng ra nước ngoài, IPO readiness\n\n' ||
    E'Fee mềm cho startup — có gói starter riêng.\n\n' ||
    E'### 6. Làm sao đặt lịch tư vấn với BSN?\n' ||
    E'3 cách:\n' ||
    E'- **Gọi hotline:** 0868 960 917 (giờ HC 8:00-17:30)\n' ||
    E'- **Email:** info@bsn.vn — phản hồi trong 24h\n' ||
    E'- **Điện thoại tổng đài:** 028 3535 6644\n\n' ||
    E'Discovery call đầu tiên (30-60 phút) **hoàn toàn miễn phí**.\n\n' ||
    E'### 7. BSN có văn phòng ở đâu?\n' ||
    E'- **Trụ sở chính:** Tầng 14, Tòa nhà HM Town, 412 Nguyễn Thị Minh Khai, Phường Bàn Cờ, TP.HCM\n' ||
    E'- **Chi nhánh:** 171-175 Hàm Nghi, Phường Nguyễn Thái Bình, TP.HCM\n\n' ||
    E'Ngoài 2 văn phòng, BSN phục vụ khách toàn quốc qua online + đi công tác đến địa phương khi cần.\n\n' ||
    E'### 8. Ngành nghề nào BSN có expertise sâu?\n' ||
    E'8 ngành:\n' ||
    E'- Sản xuất · Nông nghiệp · Bất động sản · Tài chính-Ngân hàng\n' ||
    E'- FDI · Thương mại-Dịch vụ · Công nghệ thông tin · Giáo dục-Y tế\n\n' ||
    E'Ngành F&B có **giải pháp chuyên sâu riêng** (mô hình kinh doanh, unit economics, franchise).\n\n' ||
    E'### 9. Chi phí dịch vụ BSN bao nhiêu?\n' ||
    E'Phụ thuộc scope + quy mô:\n' ||
    E'- **Kế toán dịch vụ:** từ 3-15 triệu/tháng tuỳ khối lượng giao dịch\n' ||
    E'- **Tư vấn thành lập DN:** 5-30 triệu/dự án\n' ||
    E'- **Xin visa/work permit:** 8-25 triệu/hồ sơ\n' ||
    E'- **Kiểm toán BCTC:** 30-200 triệu/kỳ\n' ||
    E'- **Tư vấn chiến lược / M&A:** 150 triệu - 2 tỷ+ tuỳ dự án\n\n' ||
    E'Báo giá cụ thể sau discovery call miễn phí.\n\n' ||
    E'### 10. BSN có ký NDA bảo mật không?\n' ||
    E'**Có, luôn luôn.** BSN chủ động đề xuất NDA 2 chiều trước khi trao đổi thông tin nhạy cảm. ' ||
    E'Đây là chuẩn nghiệp vụ của BSN — không có ngoại lệ.\n\n' ||
    E'### 11. Thanh toán như thế nào?\n' ||
    E'- Xuất **hoá đơn VAT 10%** đầy đủ (BSN có mã số thuế + hoá đơn điện tử)\n' ||
    E'- Chuyển khoản qua ngân hàng — MB Bank, Vietcombank, Techcombank\n' ||
    E'- Dự án lớn có thể chia milestone (30-50-20 hoặc theo phase)\n' ||
    E'- Dịch vụ định kỳ (kế toán, payroll): trả trước hàng tháng vào ngày 5\n\n' ||
    E'### 12. Nếu tôi chưa biết mình cần dịch vụ nào?\n' ||
    E'Không sao — chỉ cần gọi hotline **0868 960 917** hoặc email **info@bsn.vn** mô tả tình hình. ' ||
    E'BSN sẽ:\n' ||
    E'1. Sắp xếp discovery call 30-60 phút miễn phí\n' ||
    E'2. Tư vấn chỉ ra vấn đề cốt lõi bạn cần giải quyết\n' ||
    E'3. Đề xuất dịch vụ phù hợp (hoặc giới thiệu đối tác nếu ngoài scope BSN)\n\n' ||
    E'BSN cam kết **không ép bán** — nếu không phù hợp sẽ nói thẳng.'
WHERE title = 'FAQ — Câu hỏi thường gặp về BSN';

COMMIT;

-- Verify
\echo ''
\echo '=== BSN notes summary ==='
SELECT title, LENGTH(content) AS chars
FROM notes n JOIN tenants t ON t.id=n.tenant_id
WHERE t.code='bsn'
ORDER BY title;
