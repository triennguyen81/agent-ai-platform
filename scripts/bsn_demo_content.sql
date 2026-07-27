-- ============================================================================
-- BSN Demo Content — populate placeholder notes + add product notes
--
-- Assumes BSN = Vietnamese distributor of fitness / nutrition supplements
-- (whey protein, BCAA, creatine, vitamin, phụ kiện gym). This is a
-- plausible profile easy to demo. Real BSN admin can rewrite all notes
-- via /knowledge UI when they take over.
--
-- Idempotent — updates existing notes by title, inserts new ones with
-- WHERE NOT EXISTS. Safe to re-run.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Overwrite the 4 placeholder notes with real content
-- ---------------------------------------------------------------------------

UPDATE notes SET content = E'## Về BSN\n' ||
    E'**BSN Vietnam** là nhà phân phối chính thức các sản phẩm bổ sung dinh dưỡng thể thao ' ||
    E'(sports supplements) hàng đầu Việt Nam, chuyên whey protein, BCAA, creatine, vitamin và phụ kiện gym.\n\n' ||
    E'- **Website:** bsn.vn\n' ||
    E'- **Địa chỉ trụ sở:** 128 Nguyễn Huệ, Quận 1, TP.HCM\n' ||
    E'- **Chi nhánh HN:** 45 Trần Duy Hưng, Cầu Giấy, Hà Nội\n' ||
    E'- **Hotline:** 1900-BSN-VN (1900-276-86)\n' ||
    E'- **Email:** support@bsn.vn\n' ||
    E'- **MST:** 0316xxxxxx\n\n' ||
    E'## Lịch sử\n' ||
    E'BSN Vietnam thành lập năm 2018, khởi đầu với 1 showroom nhỏ tại TP.HCM. Hiện có:\n' ||
    E'- 2 chi nhánh chính (TP.HCM + Hà Nội)\n' ||
    E'- 15+ đại lý cấp 1 trên toàn quốc\n' ||
    E'- Hơn 50.000 khách hàng thân thiết\n' ||
    E'- Đội ngũ 45 nhân viên, trong đó có 8 huấn luyện viên cá nhân (PT) tư vấn dinh dưỡng miễn phí\n\n' ||
    E'## Sứ mệnh\n' ||
    E'"Mang sản phẩm chính hãng, giá tốt nhất và tư vấn khoa học đến người tập luyện Việt Nam."\n\n' ||
    E'## Giá trị cốt lõi\n' ||
    E'1. **Chính hãng 100%** — hợp đồng phân phối trực tiếp với nhà sản xuất Hoa Kỳ\n' ||
    E'2. **Giá tốt nhất** — cam kết hoàn 200% chênh lệch nếu tìm được giá thấp hơn\n' ||
    E'3. **Tư vấn miễn phí** — PT chuyên nghiệp support qua chat/hotline'
WHERE title = 'Giới thiệu công ty BSN';

UPDATE notes SET content = E'## Danh mục sản phẩm chính (2026)\n\n' ||
    E'### 🥛 WHEY PROTEIN\n' ||
    E'| Sản phẩm | Trọng lượng | Giá | Ghi chú |\n' ||
    E'|----------|-------------|-----|--------|\n' ||
    E'| Whey Gold Standard 100% (5lb) | 2.27kg | **1.850.000₫** | Best seller, ~74 khẩu phần |\n' ||
    E'| Whey Gold Standard 100% (2lb) | 907g | **890.000₫** | Cho người mới, 29 khẩu phần |\n' ||
    E'| Whey Isolate Pure (2lb) | 907g | **1.150.000₫** | Ít lactose, tinh khiết hơn |\n' ||
    E'| Whey Concentrate Economy (5lb) | 2.27kg | **1.290.000₫** | Giá tốt cho SV |\n\n' ||
    E'### 💪 BCAA / EAA / Amino\n' ||
    E'| Sản phẩm | Loại | Giá |\n' ||
    E'|----------|------|-----|\n' ||
    E'| BCAA 2:1:1 (400g) | Bột hương dâu/dưa hấu | **550.000₫** |\n' ||
    E'| EAA Complete (300g) | 9 axit amin thiết yếu | **720.000₫** |\n' ||
    E'| Amino Energy (270g) | Có caffeine, dùng trước tập | **650.000₫** |\n\n' ||
    E'### ⚡ Pre-workout / Creatine\n' ||
    E'| Sản phẩm | Giá | Công dụng |\n' ||
    E'|----------|-----|-----------|\n' ||
    E'| N.O.-Xplode (555g) | **890.000₫** | Pre-workout tăng sức bền |\n' ||
    E'| C4 Original (390g) | **650.000₫** | Pre-workout phổ biến |\n' ||
    E'| Creatine Monohydrate (500g) | **450.000₫** | Micronized, không mùi |\n\n' ||
    E'### 🍫 Bánh & Snacks\n' ||
    E'| Sản phẩm | Giá |\n' ||
    E'|----------|-----|\n' ||
    E'| Protein Bar Cookie & Cream (60g × 12 thanh) | **480.000₫** |\n' ||
    E'| Whey Cookies chocolate (100g × 6 gói) | **320.000₫** |\n\n' ||
    E'### 🧴 Vitamin & Hỗ trợ\n' ||
    E'| Sản phẩm | Giá |\n' ||
    E'|----------|-----|\n' ||
    E'| Omega-3 Fish Oil (200 viên) | **480.000₫** |\n' ||
    E'| Multivitamin Sport (90 viên) | **390.000₫** |\n' ||
    E'| ZMA (Zinc-Magnesium) 90 viên | **420.000₫** |\n\n' ||
    E'## Combo tiết kiệm\n' ||
    E'- **Combo Newbie** — Whey 2lb + Multivitamin: **1.199.000₫** (giảm 80k)\n' ||
    E'- **Combo Muscle Gain** — Whey 5lb + Creatine + BCAA: **2.599.000₫** (giảm 250k)\n' ||
    E'- **Combo Cut Fat** — Whey Iso 2lb + EAA + Fat Burner: **2.199.000₫** (giảm 200k)\n\n' ||
    E'## Chính sách giá\n' ||
    E'- Miễn ship đơn > **500.000₫** toàn quốc\n' ||
    E'- Giảm **5%** đơn > 2 triệu, **10%** đơn > 5 triệu\n' ||
    E'- Thẻ thành viên bạc/vàng/kim cương giảm 3%/5%/8% mọi đơn'
WHERE title = 'Dịch vụ / Sản phẩm chính của BSN';

UPDATE notes SET content = E'## Bảo hành sản phẩm\n\n' ||
    E'### Cam kết chính hãng 100%\n' ||
    E'BSN cam kết mọi sản phẩm bán ra đều **chính hãng**, có tem chống giả và mã QR xác thực. ' ||
    E'Nếu phát hiện hàng giả → **hoàn tiền 200%** giá trị sản phẩm.\n\n' ||
    E'### Chính sách đổi trả\n' ||
    E'| Điều kiện | Thời gian | Xử lý |\n' ||
    E'|-----------|-----------|-------|\n' ||
    E'| Sản phẩm bị lỗi nhà sản xuất (rách bao, hỏng nắp) | **7 ngày** kể từ nhận hàng | Đổi mới 100%, BSN chịu ship |\n' ||
    E'| Nhận sai sản phẩm | 7 ngày | Đổi đúng, BSN chịu ship |\n' ||
    E'| Không hợp mùi vị, chưa mở seal | 3 ngày | Đổi vị khác, khách chịu ship về |\n' ||
    E'| Đã mở seal | Không đổi trả | Trừ trường hợp lỗi sản phẩm |\n' ||
    E'| Sản phẩm hết hạn khi mở | 15 ngày | Đổi mới, BSN chịu ship |\n\n' ||
    E'### Hạn sử dụng\n' ||
    E'BSN cam kết mọi sản phẩm giao đến khách còn **HSD ≥ 12 tháng**. Nếu HSD < 12 tháng ' ||
    E'sẽ được thông báo trước + giảm giá 15-30% tuỳ mức độ.\n\n' ||
    E'## Hỗ trợ khách hàng\n\n' ||
    E'### Kênh liên hệ\n' ||
    E'- **Hotline:** 1900-276-86 (8:00-21:00, kể cả CN)\n' ||
    E'- **Zalo OA:** BSN Vietnam Official\n' ||
    E'- **Email:** support@bsn.vn (phản hồi trong 24h)\n' ||
    E'- **Fanpage:** facebook.com/bsn.vietnam\n' ||
    E'- **Chat trực tiếp** trên bsn.vn (8:00-21:00)\n\n' ||
    E'### Tư vấn PT miễn phí\n' ||
    E'Đội ngũ **8 huấn luyện viên cá nhân** sẵn sàng tư vấn:\n' ||
    E'- Chọn sản phẩm phù hợp mục tiêu (tăng cơ / giảm mỡ / duy trì)\n' ||
    E'- Cách sử dụng đúng liều lượng theo cân nặng + cường độ tập\n' ||
    E'- Lịch tập gợi ý miễn phí sau khi mua đơn đầu tiên\n\n' ||
    E'Đặt lịch: gọi hotline hoặc booking tại bsn.vn/tu-van'
WHERE title = 'Chính sách bảo hành & hỗ trợ khách hàng BSN';

UPDATE notes SET content = E'## FAQ — Câu hỏi thường gặp\n\n' ||
    E'### 1. BSN có giao hàng toàn quốc không?\n' ||
    E'**Có.** BSN giao hàng 63 tỉnh thành qua GHN + Viettel Post + J&T Express.\n' ||
    E'- Nội thành TP.HCM + Hà Nội: **1-2 ngày**, ship 25.000₫\n' ||
    E'- Các tỉnh lân cận: **2-3 ngày**, ship 30.000-40.000₫\n' ||
    E'- Miền Trung + Tây Nguyên: **3-5 ngày**, ship 40.000-50.000₫\n' ||
    E'- Miền núi + hải đảo: **5-7 ngày**, ship 50.000-70.000₫\n' ||
    E'- **Miễn ship đơn > 500.000₫** toàn quốc.\n\n' ||
    E'### 2. Thanh toán qua các phương thức nào?\n' ||
    E'BSN hỗ trợ:\n' ||
    E'- **COD** (thanh toán khi nhận hàng) — mọi tỉnh\n' ||
    E'- **Chuyển khoản** — MB Bank, Vietcombank, Techcombank, Sacombank\n' ||
    E'- **Ví điện tử** — Momo, ZaloPay, VNPay, ShopeePay\n' ||
    E'- **Thẻ ATM / Credit card** — qua cổng OnePay\n' ||
    E'- **Trả góp 0%** với đơn > 3 triệu qua HomeCredit, Kredivo\n\n' ||
    E'### 3. Sản phẩm BSN có phải hàng chính hãng không?\n' ||
    E'**Có, 100% chính hãng.** BSN Vietnam là nhà phân phối chính thức được uỷ quyền bởi ' ||
    E'BSN Sports Nutrition (Hoa Kỳ) và các thương hiệu khác. Mỗi sản phẩm có:\n' ||
    E'- Tem chống giả 7 màu\n' ||
    E'- Mã QR quét kiểm tra tại bsn.vn/xac-thuc\n' ||
    E'- Hoá đơn VAT nếu yêu cầu\n\n' ||
    E'### 4. Whey protein có xuất VAT không?\n' ||
    E'**Có.** BSN xuất hoá đơn VAT 10% cho khách yêu cầu. Vui lòng cung cấp:\n' ||
    E'- Tên công ty\n' ||
    E'- Mã số thuế\n' ||
    E'- Địa chỉ đăng ký kinh doanh\n' ||
    E'- Email nhận hoá đơn điện tử\n\n' ||
    E'Ghi rõ "xuất HĐ VAT" trong ghi chú đơn hàng hoặc gọi hotline.\n\n' ||
    E'### 5. Đổi trả trong bao lâu?\n' ||
    E'- **7 ngày** với sản phẩm lỗi/sai hàng\n' ||
    E'- **3 ngày** với đổi vị (chưa mở seal, khách chịu ship về)\n' ||
    E'- Không đổi trả nếu đã mở seal (trừ trường hợp lỗi sản phẩm)\n\n' ||
    E'### 6. Có khuyến mãi cho đơn hàng lớn không?\n' ||
    E'**Có, giảm giá theo bậc:**\n' ||
    E'- Đơn > 2.000.000₫: giảm **5%**\n' ||
    E'- Đơn > 5.000.000₫: giảm **10%**\n' ||
    E'- Đơn > 10.000.000₫: giảm **12%** + tặng bình lắc + áo BSN\n' ||
    E'- Combo tiết kiệm giảm sẵn 80.000-250.000₫ (xem note "Sản phẩm chính")\n\n' ||
    E'### 7. Làm sao để đăng ký làm đại lý BSN?\n' ||
    E'Điều kiện:\n' ||
    E'- Doanh nghiệp có ĐKKD hợp lệ\n' ||
    E'- Đơn hàng đầu tiên tối thiểu **30.000.000₫**\n' ||
    E'- Chiết khấu **15-25%** tuỳ mức doanh số\n\n' ||
    E'Liên hệ: dailyagent@bsn.vn hoặc hotline 1900-276-86 nhánh 3.\n\n' ||
    E'### 8. Whey protein có tác dụng phụ không?\n' ||
    E'Whey protein từ BSN là **thực phẩm bổ sung an toàn** khi dùng đúng liều. Không dùng nếu:\n' ||
    E'- Dị ứng lactose (chọn Whey Isolate thay vì Concentrate)\n' ||
    E'- Suy thận mãn (tham khảo bác sĩ)\n' ||
    E'- Trẻ em < 12 tuổi (dùng chế phẩm chuyên biệt)\n\n' ||
    E'### 9. Nên uống whey mấy lần/ngày?\n' ||
    E'**Người bình thường tập gym:**\n' ||
    E'- Sáng: 1 khẩu phần (25-30g protein)\n' ||
    E'- Sau tập: 1 khẩu phần\n' ||
    E'- Tổng: 25-40g protein/ngày từ whey (còn lại từ thực phẩm)\n\n' ||
    E'**Người tập nặng / muốn tăng cơ:** có thể lên 60-80g/ngày. Tư vấn PT miễn phí để đúng liều.\n\n' ||
    E'### 10. Sản phẩm bảo quản như thế nào?\n' ||
    E'- **Đặt nơi khô ráo**, tránh ánh nắng trực tiếp\n' ||
    E'- Nhiệt độ: **< 30°C** (không để trong xe hơi)\n' ||
    E'- Đậy kín nắp sau mỗi lần dùng\n' ||
    E'- Dùng thìa khô để múc, tránh nước lọt vào hộp\n' ||
    E'- Sau khi mở: dùng trong **2-3 tháng** để đảm bảo chất lượng\n\n' ||
    E'### 11. Có sản phẩm cho người ăn chay không?\n' ||
    E'**Có.** BSN phân phối các dòng plant-based:\n' ||
    E'- **Vegan Protein Blend** (đậu Hà Lan + gạo lứt + hạt hemp) — 980.000₫/900g\n' ||
    E'- **Soy Protein Isolate** — 890.000₫/1kg\n\n' ||
    E'### 12. Sản phẩm có nguồn gốc từ đâu?\n' ||
    E'- **Whey Gold Standard, N.O.-Xplode:** sản xuất tại nhà máy Optimum Nutrition (Hoa Kỳ)\n' ||
    E'- **BCAA, Creatine:** nhập từ nhà máy Glanbia (Ireland)\n' ||
    E'- **Vegan lines:** sản xuất tại Canada\n' ||
    E'- Tất cả có chứng nhận **NSF Certified for Sport** hoặc **Informed-Sport**'
WHERE title = 'FAQ — Câu hỏi thường gặp về BSN';

-- ---------------------------------------------------------------------------
-- 2. Add more domain-specific notes (product knowledge, guides)
-- ---------------------------------------------------------------------------

INSERT INTO notes (user_id, tenant_id, title, content, tags)
SELECT
    (SELECT id FROM users WHERE email='admin@bsn.vn'),
    (SELECT id FROM tenants WHERE code='bsn'),
    t.title, t.content, t.tags
FROM (VALUES
    (
        'Hướng dẫn chọn whey protein cho người mới tập',
        E'## Chọn whey protein đầu tiên — BSN gợi ý\n\n' ||
        E'### Bước 1: Xác định mục tiêu\n' ||
        E'| Mục tiêu | Sản phẩm khuyên | Lý do |\n' ||
        E'|----------|-----------------|-------|\n' ||
        E'| **Tăng cân + tăng cơ** | Whey Gold Standard 5lb + Combo Muscle Gain | Nhiều calo, protein chất lượng |\n' ||
        E'| **Giảm mỡ + giữ cơ** | Whey Isolate Pure 2lb | Ít carb, ít lactose |\n' ||
        E'| **Duy trì sức khỏe** | Whey Gold Standard 2lb | Vừa đủ, không dư thừa |\n' ||
        E'| **Người ăn chay** | Vegan Protein Blend | Từ thực vật, không sữa |\n\n' ||
        E'### Bước 2: Cân nhắc ngân sách\n' ||
        E'- **Dưới 1 triệu:** Whey Concentrate Economy 5lb (giá tốt, chất lượng ổn)\n' ||
        E'- **1-1.5 triệu:** Whey Gold Standard 2lb (best seller, đúng tiêu chuẩn)\n' ||
        E'- **1.5-2 triệu:** Whey Isolate hoặc Gold Standard 5lb\n\n' ||
        E'### Bước 3: Chọn hương vị\n' ||
        E'**Top 5 vị bán chạy nhất tại BSN:**\n' ||
        E'1. **Double Rich Chocolate** — vị chocolate đậm, phổ biến nhất\n' ||
        E'2. **Vanilla Ice Cream** — nhẹ, dễ uống, phù hợp mix sinh tố\n' ||
        E'3. **Cookies & Cream** — vị bánh Oreo, ngọt vừa\n' ||
        E'4. **Strawberry** — vị dâu, cho người không thích chocolate\n' ||
        E'5. **Rocky Road** — chocolate + marshmallow, đặc biệt\n\n' ||
        E'### Bước 4: Cách pha đúng\n' ||
        E'```\n' ||
        E'1 khẩu phần whey (~30g bột)\n' ||
        E'+ 250-300ml nước lọc HOẶC sữa tươi không đường\n' ||
        E'+ Cho vào bình lắc, lắc 15-20 giây\n' ||
        E'= Uống ngay (không để quá 30 phút sau khi pha)\n' ||
        E'```\n\n' ||
        E'### Bước 5: Timing uống whey\n' ||
        E'- **Sáng thức dậy:** giúp bổ sung protein sau đêm dài\n' ||
        E'- **Sau tập 30 phút:** thời điểm cơ bắp hấp thu tối ưu\n' ||
        E'- **Trước ngủ (tùy chọn):** nếu ăn tối ít protein\n\n' ||
        E'### ⚠ Lưu ý cho người mới\n' ||
        E'- Uống đủ nước: 2.5-3L/ngày khi bổ sung whey\n' ||
        E'- Không thay thế hoàn toàn thực phẩm — chỉ là "bổ sung"\n' ||
        E'- Nếu tiêu chảy sau khi uống → có thể dị ứng lactose → đổi sang Whey Isolate\n' ||
        E'- Tư vấn PT miễn phí: gọi hotline 1900-276-86 nhánh 2',
        ARRAY['whey','huong-dan','nguoi-moi','tu-van']::text[]
    ),
    (
        'So sánh Whey Concentrate vs Whey Isolate vs Hydrolyzed',
        E'## 3 loại whey chính — chọn loại nào?\n\n' ||
        E'| Loại | % Protein | Lactose | Giá | Ai nên dùng |\n' ||
        E'|------|-----------|---------|-----|-------------|\n' ||
        E'| **Whey Concentrate** (WPC) | 70-80% | Có (3-5g/khẩu phần) | Rẻ nhất | Người thường, ngân sách hạn chế |\n' ||
        E'| **Whey Isolate** (WPI) | 90%+ | Rất ít (< 1g) | Trung bình | Không dung nạp lactose, giảm mỡ |\n' ||
        E'| **Whey Hydrolyzed** (WPH) | 90%+ | Không | Cao nhất | VĐV chuyên nghiệp, tiêu hoá nhanh |\n\n' ||
        E'### Chi tiết\n\n' ||
        E'**1. Whey Concentrate (WPC)**\n' ||
        E'- Là dạng whey **ít qua lọc nhất**, giữ lại nhiều chất dinh dưỡng tự nhiên (BCAA, immunoglobulin)\n' ||
        E'- **Ưu:** Giá tốt, vị đậm đà tự nhiên\n' ||
        E'- **Nhược:** Chứa lactose (khoảng 3-5g/khẩu phần) → có thể gây đầy hơi cho người nhạy cảm\n' ||
        E'- **Sản phẩm BSN:** Whey Concentrate Economy 5lb — 1.290.000₫\n\n' ||
        E'**2. Whey Isolate (WPI)**\n' ||
        E'- Đã lọc qua kỹ hơn, loại bỏ hầu hết chất béo + lactose\n' ||
        E'- **Ưu:** Ít lactose, hấp thu nhanh, phù hợp giảm mỡ\n' ||
        E'- **Nhược:** Đắt hơn WPC ~30-40%\n' ||
        E'- **Sản phẩm BSN:** Whey Isolate Pure 2lb — 1.150.000₫\n\n' ||
        E'**3. Whey Hydrolyzed (WPH)**\n' ||
        E'- Đã "cắt trước" thành các peptide nhỏ → cơ thể hấp thu **nhanh nhất**\n' ||
        E'- **Ưu:** Tiêu hoá tốt, phục hồi cơ nhanh\n' ||
        E'- **Nhược:** Giá cao (~2 triệu/2lb), vị hơi đắng\n' ||
        E'- **Sản phẩm BSN:** Platinum HydroWhey 2lb — 1.950.000₫\n\n' ||
        E'### Gợi ý theo trường hợp\n' ||
        E'- **Sinh viên mới tập gym**: Concentrate (giá tốt)\n' ||
        E'- **Người bụng nhạy cảm lactose**: Isolate\n' ||
        E'- **Đang cắt mỡ, ăn kiêng**: Isolate\n' ||
        E'- **VĐV chuyên nghiệp / hồi phục sau tập nặng**: Hydrolyzed\n' ||
        E'- **Người ăn chay**: Vegan Protein Blend',
        ARRAY['whey','so-sanh','concentrate','isolate','hydrolyzed']::text[]
    ),
    (
        'Cách bảo quản whey protein + BCAA đúng cách',
        E'## Bảo quản whey protein\n\n' ||
        E'### Trước khi mở\n' ||
        E'- Đặt nơi **khô ráo, thoáng mát** (< 30°C)\n' ||
        E'- Tránh **ánh nắng trực tiếp** — UV làm biến chất protein\n' ||
        E'- KHÔNG để trong xe hơi (nhiệt lên đến 60°C mùa hè)\n' ||
        E'- KHÔNG để trong tủ lạnh (độ ẩm cao → whey bị vón cục)\n\n' ||
        E'### Sau khi mở\n' ||
        E'- **Đậy kín nắp** sau mỗi lần dùng\n' ||
        E'- Dùng **thìa khô** để múc — nước lọt vào sẽ làm mốc\n' ||
        E'- Dùng trong **2-3 tháng** để đảm bảo chất lượng\n' ||
        E'- Nếu whey bị vón cục cứng hoặc đổi màu → **KHÔNG dùng**\n\n' ||
        E'### Cách nhận biết whey hỏng\n' ||
        E'⚠ **Không dùng nếu:**\n' ||
        E'- Có mùi chua, khó chịu\n' ||
        E'- Đổi màu (chuyển sang vàng đậm, xám)\n' ||
        E'- Vón cục cứng như đá\n' ||
        E'- Có nấm mốc\n\n' ||
        E'## Bảo quản BCAA / EAA (dạng bột)\n' ||
        E'Tương tự whey — thêm lưu ý:\n' ||
        E'- BCAA hấp ẩm mạnh, nếu bột **dính lại thành khối** → vẫn dùng được nhưng khó pha\n' ||
        E'- Đặt gói hút ẩm silica gel (nếu có) trong hộp\n\n' ||
        E'## Bảo quản viên nang (Multivitamin, Fish Oil...)\n' ||
        E'- Đặt nơi mát (không cần tủ lạnh trừ Fish Oil đã mở nắp)\n' ||
        E'- Fish Oil đã mở: **đậy kín, để tủ lạnh** để tránh oxy hoá dầu\n' ||
        E'- Kiểm tra HSD trước khi dùng\n\n' ||
        E'## Bảo quản Protein Bar\n' ||
        E'- Nơi mát, tránh nắng\n' ||
        E'- Không để tủ lạnh (thanh bar sẽ cứng, khó ăn)\n' ||
        E'- Mùa nóng: có thể để ngăn mát tủ lạnh trước khi ăn 5-10 phút → dễ ăn hơn',
        ARRAY['bao-quan','whey','bcaa','huong-dan']::text[]
    ),
    (
        'Chương trình khách hàng thân thiết + đại lý BSN',
        E'## Thẻ thành viên BSN — 3 bậc\n\n' ||
        E'| Bậc | Điều kiện | Chiết khấu | Ưu đãi kèm |\n' ||
        E'|-----|-----------|------------|-------------|\n' ||
        E'| 🥉 **BẠC** | Đã mua bất kỳ đơn nào | **3%** mọi đơn | Sinh nhật tặng 100k voucher |\n' ||
        E'| 🥈 **VÀNG** | Tổng chi tiêu > 5 triệu/năm | **5%** mọi đơn | + Ship miễn phí + PT tư vấn ưu tiên |\n' ||
        E'| 🥇 **KIM CƯƠNG** | Tổng chi tiêu > 20 triệu/năm | **8%** mọi đơn | + Quà độc quyền + hotline VIP |\n\n' ||
        E'**Tự động upgrade** — không cần đăng ký. Reset mỗi năm dương lịch.\n\n' ||
        E'## Điểm thưởng BSN Points\n' ||
        E'- 1.000₫ chi tiêu = **1 điểm**\n' ||
        E'- 100 điểm = **10.000₫** giảm giá đơn tiếp theo\n' ||
        E'- Điểm không hết hạn (miễn còn active tài khoản)\n' ||
        E'- Đơn > 5 triệu tặng x2 điểm\n\n' ||
        E'## Chương trình giới thiệu bạn bè\n' ||
        E'- Bạn giới thiệu 1 người mới mua: bạn nhận **100.000₫ voucher** + bạn của bạn nhận **50.000₫**\n' ||
        E'- Không giới hạn số người\n' ||
        E'- Điều kiện: đơn đầu tiên của bạn được giới thiệu ≥ 500.000₫\n\n' ||
        E'## Chương trình đại lý\n\n' ||
        E'### Điều kiện\n' ||
        E'- Có ĐKKD (hộ kinh doanh cá thể hoặc doanh nghiệp)\n' ||
        E'- Cam kết doanh số tối thiểu:\n' ||
        E'  - Đại lý cấp 3: **10 triệu/tháng** — chiết khấu **15%**\n' ||
        E'  - Đại lý cấp 2: **30 triệu/tháng** — chiết khấu **20%**\n' ||
        E'  - Đại lý cấp 1: **100 triệu/tháng** — chiết khấu **25%** + hỗ trợ marketing\n\n' ||
        E'### Hỗ trợ đại lý\n' ||
        E'- Tài liệu bán hàng: catalogue in màu, poster, video hướng dẫn\n' ||
        E'- Training miễn phí về sản phẩm (2 buổi/năm)\n' ||
        E'- Chính sách đổi trả 30 ngày với hàng chưa mở seal\n' ||
        E'- Ưu tiên giao hàng khi hết stock\n\n' ||
        E'### Đăng ký\n' ||
        E'📧 Email: **dailyagent@bsn.vn**\n' ||
        E'☎ Hotline: **1900-276-86 nhánh 3**\n' ||
        E'Chuẩn bị: bản scan ĐKKD, kế hoạch kinh doanh sơ bộ',
        ARRAY['thanh-vien','dai-ly','chiet-khau','uu-dai']::text[]
    ),
    (
        'Địa chỉ showroom + giờ mở cửa BSN',
        E'## Showroom trực tiếp\n\n' ||
        E'### 🏙️ TP.HCM (trụ sở chính)\n' ||
        E'- **Địa chỉ:** 128 Nguyễn Huệ, phường Bến Nghé, Quận 1\n' ||
        E'- **Điện thoại:** 028-3822-1988\n' ||
        E'- **Giờ mở cửa:** 8:00-21:00 (mọi ngày, kể cả CN + lễ)\n' ||
        E'- **Bãi xe:** máy có (miễn phí 2 tiếng)\n' ||
        E'- **Tiện ích:** phòng thử sản phẩm, khu vực tư vấn PT\n\n' ||
        E'### 🏙️ Hà Nội (chi nhánh)\n' ||
        E'- **Địa chỉ:** 45 Trần Duy Hưng, phường Trung Hoà, Cầu Giấy\n' ||
        E'- **Điện thoại:** 024-3773-8899\n' ||
        E'- **Giờ mở cửa:** 8:30-21:00 (T2-CN)\n' ||
        E'- **Nghỉ:** không nghỉ (kể cả lễ Tết)\n\n' ||
        E'## Đại lý cấp 1 trên toàn quốc\n' ||
        E'- **Đà Nẵng:** 234 Nguyễn Văn Linh (ĐL Fitness Đà Nẵng)\n' ||
        E'- **Cần Thơ:** 88 Trần Hưng Đạo (ĐL Nutrition Miền Tây)\n' ||
        E'- **Hải Phòng:** 56 Lạch Tray (ĐL Gym Hải Phòng)\n' ||
        E'- **Nha Trang:** 12 Trần Phú (ĐL Sport Central)\n' ||
        E'- **Vũng Tàu:** 78 Hạ Long (ĐL BSN Vũng Tàu)\n' ||
        E'- Danh sách đầy đủ 15 đại lý: bsn.vn/dai-ly\n\n' ||
        E'## Kênh online\n' ||
        E'- Website: **bsn.vn** (24/7)\n' ||
        E'- Shopee Mall: shopee.vn/bsn_official\n' ||
        E'- Lazada Mall: lazada.vn/shop/bsn-vietnam\n' ||
        E'- Tiki Trading: tiki.vn/cua-hang/bsn\n\n' ||
        E'## Nghỉ lễ Tết\n' ||
        E'BSN **không nghỉ Tết** ở cả 2 showroom TP.HCM + Hà Nội — vẫn đón khách bình thường ' ||
        E'từ 8:00-18:00 các ngày mùng 1-5 Tết. Giao hàng online tạm nghỉ 3 ngày mùng 1-3, ' ||
        E'nhận đơn lại từ mùng 4.',
        ARRAY['showroom','dia-chi','gio-mo-cua','dai-ly']::text[]
    )
) AS t(title, content, tags)
WHERE NOT EXISTS (
    SELECT 1 FROM notes n2
    WHERE n2.tenant_id = (SELECT id FROM tenants WHERE code='bsn')
      AND n2.title = t.title
);

-- ---------------------------------------------------------------------------
-- 3. Attach the 5 new notes to bsn-assistant
-- ---------------------------------------------------------------------------
INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
SELECT
    (SELECT a.id FROM agents a JOIN tenants t ON t.id=a.tenant_id WHERE t.code='bsn' AND a.code='bsn-assistant'),
    n.id,
    (SELECT id FROM users WHERE email='admin@bsn.vn')
FROM notes n
WHERE n.tenant_id = (SELECT id FROM tenants WHERE code='bsn')
  AND n.title IN (
      'Hướng dẫn chọn whey protein cho người mới tập',
      'So sánh Whey Concentrate vs Whey Isolate vs Hydrolyzed',
      'Cách bảo quản whey protein + BCAA đúng cách',
      'Chương trình khách hàng thân thiết + đại lý BSN',
      'Địa chỉ showroom + giờ mở cửa BSN'
  )
ON CONFLICT DO NOTHING;

COMMIT;

-- Verify
\echo ''
\echo '=== BSN notes with content lengths ==='
SELECT title, LENGTH(content) AS chars
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
