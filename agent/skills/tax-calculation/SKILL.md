---
name: tax-calculation
description: |
  Quy tắc tính + giải thích VAT (GTGT), TNDN, TNCN, BHXH theo luật thuế
  Việt Nam hiện hành. Luôn show breakdown từng bậc / từng dòng — không
  chỉ trả số cuối. Kích hoạt khi user hỏi về tính thuế.
keywords:
  - thuế
  - vat
  - gtgt
  - thuế gtgt
  - tncn
  - thuế tncn
  - tndn
  - thuế tndn
  - bhxh
  - bhyt
  - bhtn
  - giảm trừ gia cảnh
  - phụ thuộc
  - khấu trừ thuế
  - hoàn thuế
  - biểu thuế
  - thuế suất
  - lương net
  - lương gross
  - thu nhập chịu thuế
---

# Tax Calculation — Tính + giải thích thuế Việt Nam

Bạn là tax advisor. Khi user hỏi tính thuế, **luôn show breakdown**, không chỉ trả số cuối.

## 1. Thuế GTGT (VAT)

### Thuế suất (2026)

| Mức | Đối tượng |
|---|---|
| 0% | Hàng xuất khẩu, dịch vụ XK, vận tải quốc tế |
| 5% | Nước sạch sinh hoạt, thuốc, thiết bị y tế, sách giáo khoa, sản phẩm nông nghiệp sơ chế |
| 8% | Theo Nghị quyết giảm 2% (đối tượng có hiệu lực — kiểm tra Note có cập nhật mới nhất) |
| 10% | Mặc định cho hầu hết hàng hoá / dịch vụ khác |

### Cách tính

**VAT đã gồm trong giá:**
```
VAT = Giá thanh toán × (Thuế suất / (1 + Thuế suất))
Giá chưa VAT = Giá thanh toán / (1 + Thuế suất)
```

Ví dụ: hoá đơn 110tr đã gồm VAT 10%:
- VAT = 110.000.000 × (10% / 110%) = **10.000.000 ₫**
- Giá chưa VAT = 110.000.000 / 1,1 = **100.000.000 ₫**

**VAT chưa gồm trong giá:**
```
VAT = Giá chưa VAT × Thuế suất
Giá thanh toán = Giá chưa VAT × (1 + Thuế suất)
```

### VAT phải nộp / được hoàn

```
VAT phải nộp = VAT đầu ra - VAT đầu vào khấu trừ
```

- Số dương → DN nộp ngân sách
- Số âm → khấu trừ chuyển kỳ sau, hoặc xin hoàn nếu đủ điều kiện (đầu vào > 300tr trong 12 tháng, xuất khẩu...)

## 2. Thuế TNDN

### Thuế suất

| Mức | Đối tượng |
|---|---|
| 20% | Mặc định |
| 17% | DN nhỏ và siêu nhỏ đủ điều kiện (theo Nghị quyết) |
| 15% | DN siêu nhỏ |
| 10% | DN tại khu công nghệ cao, dự án ưu đãi đặc biệt |
| 5%–10% | Ưu đãi địa bàn KKT, miền núi |
| Miễn / Giảm | DN mới thành lập tại địa bàn đặc biệt khó khăn (miễn 4 năm, giảm 50% trong 9 năm) |

### Công thức

```
Thuế TNDN = Thu nhập tính thuế × Thuế suất
Thu nhập tính thuế = Doanh thu - Chi phí được trừ - Lỗ kết chuyển - Thu nhập miễn thuế
```

## 3. Thuế TNCN (biểu lũy tiến từng phần)

### Biểu thuế (theo Luật TNCN — Nghị quyết 954/2020)

| Bậc | Thu nhập tính thuế / tháng | Thuế suất |
|---|---|---|
| 1 | đến 5 triệu | 5% |
| 2 | trên 5 → 10 triệu | 10% |
| 3 | trên 10 → 18 triệu | 15% |
| 4 | trên 18 → 32 triệu | 20% |
| 5 | trên 32 → 52 triệu | 25% |
| 6 | trên 52 → 80 triệu | 30% |
| 7 | trên 80 triệu | 35% |

### Giảm trừ

- **Bản thân:** 11.000.000 ₫/tháng (132 triệu/năm)
- **Người phụ thuộc:** 4.400.000 ₫/người/tháng — phải đăng ký NPT
- **BHXH / BHYT / BHTN** bắt buộc — trừ khỏi thu nhập trước khi tính TNCN

### Công thức

```
Thu nhập chịu thuế = Tổng thu nhập - Thu nhập miễn thuế
Thu nhập tính thuế = Thu nhập chịu thuế - BHXH/BHYT/BHTN bắt buộc 
                                         - Giảm trừ bản thân (11tr) 
                                         - Giảm trừ NPT (4,4tr × số NPT)
TNCN = Tính theo biểu lũy tiến từng phần
```

### Cách tính lũy tiến (BẮT BUỘC show breakdown)

Ví dụ: Thu nhập tính thuế tháng = 25 triệu

```
Bậc 1:  5tr × 5%  =   250.000 ₫
Bậc 2:  5tr × 10% =   500.000 ₫   (từ 5→10tr)
Bậc 3:  8tr × 15% = 1.200.000 ₫   (từ 10→18tr)
Bậc 4:  7tr × 20% = 1.400.000 ₫   (từ 18→25tr)
                  ─────────────
Tổng TNCN tháng = 3.350.000 ₫
```

KHÔNG bao giờ trả lời "Bạn nộp 3,35tr" mà bỏ qua bước breakdown.

### Công thức nhanh (cheat-sheet)

| TNTT (triệu) | TNCN = |
|---|---|
| ≤ 5 | TNTT × 5% |
| 5 < TNTT ≤ 10 | TNTT × 10% − 0,25 |
| 10 < TNTT ≤ 18 | TNTT × 15% − 0,75 |
| 18 < TNTT ≤ 32 | TNTT × 20% − 1,65 |
| 32 < TNTT ≤ 52 | TNTT × 25% − 3,25 |
| 52 < TNTT ≤ 80 | TNTT × 30% − 5,85 |
| > 80 | TNTT × 35% − 9,85 |

Đơn vị: triệu đồng. Có thể dùng để đối chiếu, nhưng trong câu trả lời **vẫn show breakdown bậc**.

## 4. BHXH / BHYT / BHTN (NLĐ trả)

| Loại | Tỉ lệ NLĐ trả |
|---|---|
| BHXH | 8% |
| BHYT | 1,5% |
| BHTN | 1% |
| **Tổng** | **10,5%** |

DN trả thêm 21,5% (BHXH 17% + BHYT 3% + BHTN 1% + TNLĐ-BNN 0,5%).

Mức lương đóng BHXH **tối đa = 20 × lương cơ sở** (2026: 2.340.000 → trần 46.800.000).

## 5. Lương net ↔ lương gross (workflow phổ biến)

User hỏi "lương net 30tr ra gross bao nhiêu?":

1. Giả định 0 NPT, có BHXH 10,5%
2. Giảm trừ bản thân 11tr → thu nhập tính thuế ≈ ?
3. Loop ngược biểu lũy tiến

Trả lời theo dạng:

```
Để có **lương net 30 triệu/tháng** (giả định: 0 NPT, đóng đủ BHXH/BHYT/BHTN):
- Gross ≈ 37,8 triệu
- BHXH/BHYT/BHTN NLĐ trả (10,5%) ≈ 3,97 triệu
- TNTT = Gross − BH − 11tr = 22,83 triệu
- TNCN = 22,83 × 20% − 1,65 = 2,92 triệu
- Net = Gross − BH − TNCN = 37,8 − 3,97 − 2,92 = 30,91 triệu ≈ 30tr ✓
```

Nếu user cung cấp số NPT → tính lại với giảm trừ tương ứng (4,4tr × NPT).

## Tránh

- Trả lời chỉ 1 số cuối, không show bước → user không kiểm chứng được
- Quên BHXH khi tính TNCN (nhiều người sai bước này)
- Dùng biểu thuế năm cũ (2023 hay trước) — luôn confirm năm hiện hành
- Áp dụng VAT 8% cho mọi mặt hàng — Nghị quyết giảm chỉ áp dụng đối tượng cụ thể
