-- Seed 14 sample dentistry notes for tuong247@gmail.com
-- Run on cosmos:
--   cat infra/backup/seed-dental-notes.sql | ssh contabo-cosmos "sudo docker exec -i aiq-postgres psql -U aiq -d aiq"

BEGIN;

WITH owner AS (
    SELECT id FROM users WHERE email = 'tuong247@gmail.com' LIMIT 1
)
INSERT INTO notes (user_id, title, content, tags)
SELECT (SELECT id FROM owner), title, content, tags
FROM (VALUES
(
'Quy trình vệ sinh răng miệng hàng ngày',
$body$1. Chải răng 2 lần/ngày, mỗi lần 2 phút.
   - Bàn chải lông mềm, đổi mỗi 3 tháng hoặc khi lông bị xòe.
   - Kem đánh răng chứa fluoride 1000-1500 ppm.
   - Kỹ thuật Bass cải tiến: nghiêng 45° so với viền nướu, rung nhẹ.

2. Sử dụng chỉ nha khoa (floss) tối thiểu 1 lần/ngày, ưu tiên trước khi đi ngủ.
   - Dài 45 cm, quấn quanh ngón giữa.
   - Trượt nhẹ giữa kẽ răng, ôm hình C quanh thân răng.

3. Súc miệng bằng nước muối sinh lý 0.9% hoặc nước súc miệng không cồn.
4. Khám răng định kỳ mỗi 6 tháng, lấy cao răng 6-12 tháng/lần.$body$,
ARRAY['prevention','hygiene','daily-care']
),
(
'Sâu răng — nguyên nhân và phân loại',
$body$## Cơ chế

Sâu răng = vi khuẩn (chủ yếu Streptococcus mutans) lên men carbohydrate → tạo acid → demineralization men răng → tạo lỗ sâu.

## Yếu tố nguy cơ

- Vệ sinh răng miệng kém
- Chế độ ăn nhiều đường, ăn vặt thường xuyên
- Thiếu fluoride
- Khô miệng (xerostomia)
- Tuột nướu để lộ chân răng

## Phân loại theo ICDAS

| Code | Mô tả |
|------|-------|
| 0 | Răng lành mạnh |
| 1 | Đổi màu nhẹ khi thổi khô |
| 2 | Đổi màu rõ khi ướt |
| 3 | Vỡ men nhưng chưa thấy ngà |
| 4 | Bóng đen ngà phía dưới |
| 5 | Lỗ sâu rõ, lộ ngà |
| 6 | Lỗ sâu lan rộng, lộ tủy |

## Điều trị theo độ sâu

- Code 1-2: tái khoáng hóa bằng fluoride varnish, kem đánh răng fluor cao
- Code 3-4: trám composite hoặc GIC
- Code 5: trám composite/inlay/onlay
- Code 6: điều trị tủy + bọc chụp$body$,
ARRAY['caries','diagnosis','ICDAS']
),
(
'Cấy ghép implant nha khoa — quy trình tổng quan',
$body$## Chỉ định
- Mất 1 hoặc nhiều răng
- Không muốn mài răng bên cạnh (so với cầu răng)
- Có đủ xương hàm hoặc có thể ghép xương

## Chống chỉ định
- Bệnh nhân đang xạ trị vùng hàm mặt
- Đái tháo đường chưa kiểm soát (HbA1c > 8%)
- Hút thuốc lá nặng (> 10 điếu/ngày)
- Loãng xương nặng đang dùng bisphosphonate IV

## Quy trình tiêu chuẩn (3-6 tháng)

**Giai đoạn 1 — Khám và chẩn đoán**
- Chụp CBCT đánh giá xương hàm
- Lập kế hoạch điều trị 3D, máng hướng dẫn phẫu thuật

**Giai đoạn 2 — Cấy trụ implant** (45-90 phút)
- Gây tê tại chỗ
- Khoan định vị, tạo lỗ implant
- Đặt implant vào xương hàm
- Khâu vạt nướu

**Giai đoạn 3 — Tích hợp xương** (3-6 tháng)
- Implant tích hợp với xương (osseointegration)
- Tránh nhai mạnh bên cấy

**Giai đoạn 4 — Lắp abutment + mão sứ** (2-4 tuần)
- Lấy dấu, làm mão sứ
- Lắp abutment, gắn mão

## Chi phí tham khảo (VN, 2026)
- Implant thân thiện: 18-25 triệu/trụ (Dentium, Mis, Osstem)
- Implant cao cấp: 30-50 triệu/trụ (Nobel Biocare, Straumann)
- Bao gồm mão sứ + phụ kiện$body$,
ARRAY['implant','surgery','treatment-plan']
),
(
'Tẩy trắng răng — phương pháp và lưu ý',
$body$## Cơ chế
Hoạt chất hydrogen peroxide (H₂O₂) hoặc carbamide peroxide phá vỡ liên kết các phân tử màu trên men và ngà răng.

## Các phương pháp

### 1. Tại phòng khám (in-office)
- Gel H₂O₂ 35-40% + đèn LED/laser
- 60-90 phút, sáng 4-8 tông
- Chi phí: 2-5 triệu VND
- Phù hợp khi cần nhanh, kết quả đồng đều

### 2. Tại nhà có máng (take-home)
- Carbamide peroxide 10-22% trong máng nhựa custom
- Đeo 2-8 giờ/ngày trong 1-2 tuần
- Chi phí: 1.5-3 triệu VND
- Kết quả từ từ, ít ê buốt hơn

### 3. Kết hợp (combo)
- Tẩy in-office + take-home maintenance
- Hiệu quả tốt nhất, duy trì lâu

## Chống chỉ định

- Phụ nữ có thai và cho con bú
- Răng trẻ em dưới 16 tuổi
- Răng quá nhạy cảm
- Răng có nhiều miếng trám composite mặt trước
- Răng đã điều trị tủy (cần phương pháp internal bleaching)

## Tác dụng phụ thường gặp
- Ê buốt răng (50-80% bệnh nhân) — giảm bằng kem chống nhạy cảm potassium nitrate
- Kích ứng nướu — do gel tiếp xúc nướu, dùng cofferdam hoặc Opal Dam che chắn
- Hiệu quả giảm dần 1-3 năm, cần bảo trì$body$,
ARRAY['cosmetic','whitening','peroxide']
),
(
'Niềng răng — so sánh các phương pháp',
$body$| Phương pháp | Chi phí (VND) | Thời gian | Ưu điểm | Nhược điểm |
|-------------|---------------|-----------|---------|------------|
| Mắc cài kim loại | 25-45 triệu | 18-24 tháng | Rẻ, hiệu quả mọi case | Thẩm mỹ kém, dễ kích ứng |
| Mắc cài sứ | 40-60 triệu | 18-24 tháng | Thẩm mỹ hơn | Dễ vỡ, dễ ố vàng |
| Mắc cài tự buộc | 45-70 triệu | 14-20 tháng | Ít ma sát, ít tái khám | Đắt hơn |
| Mắc cài mặt trong (lingual) | 80-120 triệu | 24-36 tháng | Hoàn toàn ẩn | Đắt, khó vệ sinh, phát âm khó tuần đầu |
| Khay trong suốt (Invisalign) | 60-120 triệu | 12-24 tháng | Ẩn, tháo lắp, ăn uống dễ | Đòi hỏi BN kỷ luật (đeo 22h/ngày) |

## Tiến trình điều trị chung

1. **Khám và chẩn đoán** (1-2 buổi)
   - Chụp X-quang panoramic + cephalometric
   - Lấy dấu hoặc scan 3D
   - Lập kế hoạch số hóa

2. **Tiền điều trị**
   - Vệ sinh răng, lấy cao
   - Trám các răng sâu
   - Nhổ răng nếu cần (răng số 4 thường nhổ trong case crowding nặng)

3. **Gắn khí cụ** (mắc cài/khay)
4. **Tái khám định kỳ** mỗi 4-6 tuần
5. **Tháo khí cụ + duy trì** với hàm duy trì 12-24 tháng đầu$body$,
ARRAY['orthodontics','braces','invisalign']
),
(
'Quy trình tiệt trùng dụng cụ nha khoa',
$body$Tuân thủ chuẩn CDC 2019 + Bộ Y tế VN 2018.

## Bước 1: Tiền xử lý ngay sau dùng
- Lau sạch máu, mủ bằng khăn ẩm
- Ngâm trong dung dịch tẩy enzyme (Cidezyme, MetriZyme) 15 phút

## Bước 2: Làm sạch
- Rửa siêu âm (Ultrasonic) 10-15 phút trong dung dịch tẩy
- Rửa lại nước cất, lau khô bằng vải không xơ

## Bước 3: Đóng gói
- Túi tự dán có chỉ thị nhiệt
- Ghi ngày tiệt trùng, hạn dùng (30 ngày)

## Bước 4: Tiệt trùng
| Phương pháp | Nhiệt độ | Thời gian | Loại dụng cụ |
|-------------|----------|-----------|--------------|
| Autoclave (steam) class B | 134°C | 4-7 phút | Hầu hết kim loại |
| Autoclave class N | 121°C | 15-20 phút | Dụng cụ không lòng |
| Dry heat | 160-180°C | 30-120 phút | Dụng cụ kỵ nhiệt ẩm |
| Plasma H₂O₂ | 50-65°C | 35-75 phút | Nhựa, cao su |

## Bước 5: Lưu trữ
- Tủ inox kín, khô, sạch
- Tránh ánh nắng trực tiếp
- Kiểm tra túi: nếu rách, ướt, hết hạn → tiệt trùng lại

## Indicator (chỉ thị)

- Hóa học class 1 (bên ngoài): chỉ ra túi đã đi qua chu kỳ
- Hóa học class 5 (bên trong): kiểm tra cả T, P, thời gian
- Sinh học (Bacillus stearothermophilus): test ít nhất 1 lần/tuần

## Tài liệu lưu

- Sổ tiệt trùng: ngày, mã chu kỳ, người thao tác, kết quả test
- Lưu tối thiểu 5 năm$body$,
ARRAY['sterilization','infection-control','sop']
),
(
'Vật tư tiêu hao thường dùng — danh mục nhanh',
$body$## Composite (trám thẩm mỹ)
- 3M Filtek Z350 XT — universal, đa dạng màu
- Tokuyama Estelite Sigma Quick — bóng tự nhiên, ít co
- Kerr Herculite Ultra — chịu lực tốt

## GIC (Glass Ionomer Cement)
- Fuji IX GP (GC) — trám răng sữa, abutment
- Ketac Molar (3M) — phục hồi tạm thời

## Chỉ chân răng — Cord
- Ultrapak (Ultradent) số 00, 0, 1, 2
- Dùng kèm Astringedent F hoặc ViscoStat hỗ trợ co nướu

## Vật liệu lấy dấu
- Silicone đặc Bisico Adisil + lỏng Putty
- PVS Imprint 4 (3M)
- Alginate Cavex Orthotrace — lấy dấu hàm

## Gây tê
- Lidocaine 2% + Adrenaline 1:80,000 (Septanest) — tiêu chuẩn
- Articaine 4% + Adrenaline 1:100,000 (Septocaine) — case khó tê
- Mepivacaine 3% (Carbocaine) — bệnh nhân tim mạch

## Khử trùng bề mặt
- CaviCide / CaviWipes — wipe bề mặt sau mỗi BN
- Dung dịch chlorhexidine 2% — súc miệng BN trước thủ thuật
- Cồn 70% isopropyl — wipe nhanh tay/dụng cụ nhẹ

## Đơn vị mua thường dùng (VN)
- Nha khoa Sài Gòn, Nha khoa Đại Việt, Dr. Smile, Henry Schein VN$body$,
ARRAY['materials','supplies','brands']
),
(
'Bàn ghế nha khoa — phân khúc và thương hiệu',
$body$## Phân khúc theo giá

### Phổ thông (60-150 triệu/ghế)
- Suntem ST-D560 / D580 (TQ) — entry, ổn định
- Tianjin Toption TS-308 — bán chạy phòng khám mới mở
- Foshan A1 / A2 — học việc, vùng tỉnh
- Tính năng: ghế điện, đèn halogen, súc miệng, bàn dụng cụ 5 head

### Trung cấp (150-350 triệu)
- Anya AY-A4800 (TQ premium) — thiết kế Italy, đèn LED
- Gnatus G3 / G4 (Brazil) — bền, được nhiều BS chọn
- Ritter Olympus — Đức, đầu tư dài hạn

### Cao cấp (350 triệu - 1.5 tỷ)
- Sirona TENEO / INTEGO Pro (Đức) — chuẩn vàng
- A-dec 500 (Mỹ) — ergonomic xuất sắc
- Planmeca Compact i (Phần Lan) — tích hợp scanner

## Tiêu chí chọn
1. Tải trọng (≥ 130 kg để phục vụ BN nặng cân)
2. Đèn: LED tốt hơn halogen (ít nhiệt, tiết kiệm điện)
3. Hệ thống nước: chai (clean water) tốt hơn line trực tiếp
4. Bảo hành: tối thiểu 2 năm, 24h SLA hỗ trợ
5. Bộ phận thay thế có sẵn ở VN

## Bộ phận hao mòn cần thay định kỳ
- Bóng đèn: 1-2 năm
- Tay nắm + dây dẫn nước: 2-3 năm
- Filter nước: 6 tháng
- Bơm chân không: 5-7 năm$body$,
ARRAY['equipment','dental-chair','vendor']
),
(
'Tay khoan nha khoa — high speed & low speed',
$body$## High Speed Handpiece (tay khoan nhanh — 300,000-450,000 rpm)

### Phân loại theo cơ chế
- Air turbine — phổ biến nhất, dùng khí nén
- Electric high-speed — moment lực cao, êm, ít nhiệt — đắt hơn

### Thương hiệu chính
| Brand | Model | Giá VND | Đặc điểm |
|-------|-------|---------|----------|
| NSK | Pana-Max2 | 4-6 triệu | Tốt nhất cho budget |
| NSK | S-Max M600L | 12-15 triệu | LED, ceramic bearing |
| KaVo | EXPERTtorque E680L | 25-30 triệu | Đức, êm |
| W&H | Synea Fusion | 18-22 triệu | Áo, push-button |
| Sirona | T1 Boost | 22-28 triệu | Tích hợp Sirona unit |

### Tiêu chuẩn cần kiểm tra
- Áp suất khí: 2.5-3.5 bar
- Nước phun: 25-50 ml/phút
- Tiếng ồn: < 70 dB
- Có thể sterilize (autoclavable 135°C)

## Low Speed Handpiece (tay khoan chậm — 5,000-40,000 rpm)

### Bộ 3 phần tử
- Motor (E-type ISO 3964) — chung cho mọi đầu
- Straight handpiece — dùng phẫu thuật, tháo crown
- Contra-angle — trám, đánh bóng

### Khuyến nghị
- NSK Ti-Max NL95 / NL400 (4:1, 16:1, 20:1, 64:1)
- W&H WS-75
- Bien-Air Micromotor MX2

## Bảo dưỡng

| Tần suất | Việc cần làm |
|----------|--------------|
| Sau mỗi BN | Lau ngoài bằng CaviWipes |
| Cuối ngày | Tra dầu (PANA Spray / KaVo Spray) + autoclave |
| Hàng tuần | Kiểm tra bearing (lắc nhẹ chuck, không có rơ) |
| 6 tháng | Bảo dưỡng chuyên sâu tại hãng |

KHÔNG dùng dầu xe máy hoặc WD-40 — sẽ vỡ ceramic bearing trong 1-2 tuần.$body$,
ARRAY['handpiece','equipment','maintenance']
),
(
'Máy quét intraoral scanner — so sánh và lựa chọn',
$body$## Tổng quan

Scanner thay alginate/silicone lấy dấu → xuất file STL trực tiếp vào CAD design.

## Các dòng phổ biến tại VN (2026)

### Premium
- 3Shape TRIOS 5 Wireless — 600-750 triệu
  - Không dây, color, AI bite analysis
  - Tích hợp Smile Design tốt nhất
- iTero Element 5D Plus — 550-680 triệu (Align Technology)
  - Bắt buộc nếu làm Invisalign
  - Có NIRI detect caries không cần X-ray
- Medit i900 — 350-450 triệu
  - Cost-performance tốt nhất 2026
  - Phần mềm Medit Link free
- Carestream CS 3700 — 300-380 triệu

### Entry
- Shining 3D Aoralscan 3 / 3 Wireless — 180-280 triệu
- Launca DL206 / DL208 — 150-220 triệu (TQ)
- Runyes 3D — 130-180 triệu

## Tiêu chí so sánh

| Tiêu chí | Premium | Entry |
|----------|---------|-------|
| Độ chính xác | 5-15 μm | 20-40 μm |
| Tốc độ quét full arch | 30-60s | 60-120s |
| AI feature | Có (caries, margin) | Hạn chế |
| License phần mềm | Tính thêm hoặc free | Thường free |
| ROI (BN cần) | 5-10 case/tháng | 2-5 case/tháng |

## Use case kinh tế

- Inlay/Onlay/Crown đơn: Aoralscan 3 đủ
- Bridge dài, full mouth rehab: 3Shape TRIOS hoặc iTero
- Invisalign provider: bắt buộc iTero
- Implant guided surgery: 3Shape + exoplan
- Phòng học: Medit i700 (cost/performance + xuất phòng học free)

## Tài liệu cần để scan tốt
- Răng đã làm sạch, khô (cofferdam hoặc gauze)
- Anti-fog spray cho tip scanner
- Ánh sáng phòng giảm bớt (light interference)$body$,
ARRAY['scanner','digital','equipment']
),
(
'Catalogue trám composite — màu và độ trong',
$body$## Hệ thống màu Vita Classical (16 màu)

A series (Reddish brown): A1, A2, A3, A3.5, A4
B series (Reddish yellow): B1, B2, B3, B4
C series (Greyish): C1, C2, C3, C4
D series (Reddish grey): D2, D3, D4

→ Người Việt thường dùng: A1, A2, A3 (90% case)

## Hệ thống Vita 3D-Master (Lightness × Chroma × Hue)

Chính xác hơn — 26 shade, đo bằng máy VITA Easyshade.

## Composite layering (kỹ thuật phân lớp)

### Lớp ngà (dentin layer)
- Bão hòa màu, opacity cao
- Đặt phía trong cùng, tạo nền cơ bản
- Sản phẩm: Filtek Z350XT A3 Dentin, Estelite Sigma Quick A3D

### Lớp men (enamel layer)
- Bán trong, không bão hòa màu
- Đặt ngoài, tạo độ sống động
- Sản phẩm: Filtek Z350XT A2 Body / Enamel, Estelite A2E

### Lớp hiệu ứng (effect)
- White opal (chỉnh điểm trắng cạnh răng)
- Tints (Blue, Grey — vùng cạnh răng cửa)
- Sản phẩm: Kerr Herculite Ultra Trans, Renamel Microhybrid

## Bảng tham khảo độ tuổi → màu chọn

| Tuổi BN | Màu phổ biến | Chú ý |
|---------|--------------|-------|
| 18-25 | A1, B1 | Răng còn tươi, men trong |
| 26-40 | A2, A3 | Bắt đầu mòn men nhẹ |
| 41-60 | A3, A3.5 | Ngà lộ nhiều hơn, vàng nhẹ |
| 60+ | A3.5, A4 | Mòn rõ, đôi khi cần custom |

## Quy tắc 1-2-1

- 1 lớp ngà (1.5-2 mm)
- 2 lớp men (mỗi lớp ≤ 2 mm)
- 1 lớp hiệu ứng nếu cần (rất mỏng)

→ Mỗi lớp polymerize riêng (đèn LED 1100-1500 mW/cm², 20s)

## Sản phẩm khuyến nghị 2026

- 3M Filtek Z350 XT — universal, top choice
- Tokuyama Estelite Sigma Quick — bóng tự nhiên, polish dễ
- Kerr Vertise Flow — flowable self-adhesive (lining)
- 3M Filtek One Bulk Fill — đổ bulk 4-5mm, tiết kiệm thời gian$body$,
ARRAY['composite','shade','layering']
),
(
'WHO 5 Moments — quy trình rửa tay y tế',
$body$## 5 thời điểm bắt buộc rửa tay (WHO)

1. Trước khi tiếp xúc bệnh nhân — khi vào phòng khám, trước khi bắt tay BN
2. Trước khi thực hiện thủ thuật vô khuẩn — đặt implant, đặt máng, lấy dấu
3. Sau khi tiếp xúc với dịch cơ thể — máu, nước bọt, mủ, dịch nướu
4. Sau khi tiếp xúc bệnh nhân — khi kết thúc khám/điều trị
5. Sau khi tiếp xúc môi trường xung quanh bệnh nhân — chạm vào bàn dụng cụ, ghế nha khoa

## Lựa chọn

### Hand rub (cồn xoa tay)
- Khi tay nhìn không bẩn
- Cồn ethanol 70-80% / isopropyl 60-75%
- 20-30 giây, đủ tay ướt cồn
- Sản phẩm: Microshield 4, Sterillium, AlcoStop

### Rửa tay xà phòng
- Khi tay bẩn nhìn thấy được, sau khi xử lý máu/mủ
- Xà phòng kháng khuẩn chlorhexidine 4% (Hibiscrub, Microshield)
- 40-60 giây kỹ thuật 6 bước WHO

## Kỹ thuật 6 bước WHO

1. Lòng bàn tay vào lòng bàn tay
2. Lòng bàn tay phải lên mu tay trái + đổi
3. Lòng tay + lòng tay, các ngón đan vào nhau
4. Mặt sau các ngón + lòng bàn tay đối diện (kẹp lại)
5. Xoay ngón cái phải bằng lòng tay trái + đổi
6. Đầu ngón tay chà tròn vào lòng bàn tay đối diện

Mỗi bước ≥ 5 lần.

## Lưu ý đặc biệt nha khoa

- Tháo nhẫn, đồng hồ trước khi vào phòng khám
- Móng tay ngắn, không sơn
- Vết thương hở phải băng kín (Tegaderm, Opsite)
- Trước khi gắp dụng cụ vô khuẩn: rửa tay + đeo găng vô khuẩn mới

## Audit compliance

- Mục tiêu: ≥ 80% tuân thủ
- Camera ngầm hoặc observation card random
- Báo cáo hàng quý$body$,
ARRAY['infection-control','hand-hygiene','who']
),
(
'Phân loại + xử lý chất thải y tế (TT 20/2021)',
$body$## Phân loại 5 nhóm theo Thông tư 20/2021/TT-BYT

### Nhóm 1 — Lây nhiễm sắc nhọn (vàng + biểu tượng kim)
- Kim tiêm, dao mổ, lưỡi sắc
- Mảnh thủy tinh có máu
- Đặt trong hộp chống đâm thủng (sharp container)
- Đầy 3/4 → đóng kín, nhãn tên cơ sở + ngày

### Nhóm 2 — Lây nhiễm không sắc nhọn (vàng)
- Gauze, bông gòn dính máu
- Găng tay, khẩu trang đã dùng
- Cofferdam, túi nilon trải bàn
- Túi 2 lớp PE màu vàng, miệng buộc chặt
- Lưu ≤ 48h (mùa hè) / ≤ 72h (mùa đông)

### Nhóm 3 — Giải phẫu (vàng)
- Răng nhổ, mô nướu cắt bỏ
- Đặt vào lọ thủy tinh có nắp + formalin 10%
- Hoặc túi vàng riêng

### Nhóm 4 — Hóa học nguy hại (đen)
- Thủy ngân amalgam (cực kỳ độc!)
- Dung dịch X-ray developer/fixer
- Composite chưa cure, monomer
- Thùng đậy kín + nhãn hóa chất

### Nhóm 5 — Phóng xạ (đỏ)
- Hiếm ở phòng nha khoa thông thường
- Nếu có nguồn phóng xạ → tuân thủ riêng

### Nhóm thông thường (xanh dương)
- Giấy báo, hộp carton bao bì
- Có thể tái chế/đốt rác sinh hoạt

## Quy trình xử lý amalgam (đặc biệt)

Thủy ngân kim loại = chất độc thần kinh nặng.

1. Trang bị bẫy amalgam (amalgam separator) tại line nhổ + ghế
   - Hiệu suất ≥ 95% (chuẩn ISO 11143)
   - Thay capsule 6-12 tháng
2. Mảnh amalgam thừa → lọ thủy tinh ngập nước
3. Không xả vào cống → ô nhiễm sông hồ
4. Đơn vị thu gom: URENCO, An Sinh Môi Trường, ký hợp đồng định kỳ

## Sổ theo dõi chất thải

Lưu tối thiểu 3 năm:
- Ngày phát sinh, khối lượng/nhóm
- Mã định danh túi (barcode)
- Người phụ trách giao
- Đơn vị thu gom, ngày bàn giao, ký nhận

## Đào tạo

- Toàn bộ nhân viên: 1 buổi/năm
- Nhân viên mới: trong tuần đầu nhận việc
- Lưu hồ sơ đào tạo (chữ ký, slide, bài test)$body$,
ARRAY['waste-management','infection-control','regulation']
),
(
'Xử lý sự cố tai nạn nghề nghiệp — kim đâm và phơi nhiễm',
$body$## Phòng ngừa trước hết

- KHÔNG đậy nắp kim 2 tay (one-hand recapping nếu bắt buộc)
- Bỏ kim vào sharp container NGAY sau khi rút khỏi BN
- Đeo kính bảo hộ + khẩu trang chống bắn dịch
- Đeo 2 lớp găng cho thủ thuật dài, nguy cơ cao

## Khi bị kim đâm hoặc dính dịch

### Bước 1 — Sơ cứu tại chỗ (trong 5 phút)

Da bị kim đâm:
- KHÔNG cố nặn máu mạnh
- Rửa vùng đâm với nhiều nước + xà phòng kháng khuẩn 5-10 phút
- Sát trùng bằng cồn 70% hoặc Povidone-iodine 10%

Niêm mạc bắn (mắt, mũi, miệng):
- Rửa mắt bằng nước muối 0.9% hoặc nước sạch ≥ 15 phút
- Niêm mạc miệng: súc nước sạch nhiều lần, không nuốt

Da nguyên vẹn dính máu:
- Rửa xà phòng + nước
- Tham vấn chuyên khoa nhiễm trùng nếu nguồn dương HIV/HBV/HCV

### Bước 2 — Đánh giá nguy cơ (trong 1 giờ)

1. Bệnh nhân nguồn:
   - Test HIV (rapid), HBsAg, anti-HCV
   - Nếu BN dương tính HIV: bắt đầu PEP càng sớm càng tốt
2. Người bị phơi nhiễm:
   - Tình trạng tiêm vaccine HBV (anti-HBs ≥ 10 mIU/ml)
   - Test baseline: HIV, HBsAg, anti-HBs, anti-HCV

### Bước 3 — Điều trị dự phòng

HIV — PEP (Post-Exposure Prophylaxis):
- Khởi trị trong vòng 1-2 giờ (lý tưởng), trễ nhất 72 giờ
- Phác đồ: TDF + 3TC + DTG × 28 ngày
- Liên hệ Khoa nhiễm BV Bệnh Nhiệt Đới (HN) / BV Bệnh Nhiệt Đới TP.HCM

HBV:
- Đã tiêm vaccine + đáp ứng tốt: chỉ test lại
- Chưa tiêm/không đáp ứng: HBIG 0.06 ml/kg + vaccine HBV ngay

HCV:
- Chưa có PEP đặc hiệu
- Theo dõi anti-HCV + HCV RNA tại 1, 3, 6 tháng

### Bước 4 — Theo dõi định kỳ

| Thời điểm | Test |
|-----------|------|
| Ngay sau (baseline) | HIV, HBsAg, anti-HCV |
| 6 tuần | HIV |
| 12 tuần | HIV, anti-HCV |
| 6 tháng | HIV, anti-HCV |

### Bước 5 — Báo cáo

- Báo Trưởng khoa / Giám đốc trong 24h
- Sổ tai nạn nghề nghiệp (lưu 30 năm)
- Báo BHXH nếu cần chế độ tai nạn lao động

## Số điện thoại khẩn cấp (VN)

- BV Bệnh Nhiệt Đới TW Hà Nội: 024-3576-8569
- BV Bệnh Nhiệt Đới TP.HCM: 028-3924-1873
- Tổng đài tư vấn HIV/AIDS 1800-1108 (miễn phí)$body$,
ARRAY['occupational-safety','needlestick','pep']
)
) AS seed(title, content, tags);

SELECT COUNT(*) AS notes_after_insert FROM notes
WHERE user_id = (SELECT id FROM users WHERE email = 'tuong247@gmail.com');

COMMIT;
