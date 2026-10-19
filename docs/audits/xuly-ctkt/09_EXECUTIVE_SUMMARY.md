# 09 — Executive Summary

**Phạm vi:** toàn bộ `docs/Xuly-ctkt.pdf` (7 trang), đối chiếu với code hiện tại của AIQ (FE / Gateway / DB / Agent).

**Phương pháp:**
- Đọc code: STATIC.
- Chạy test trên Postgres cô lập: TEST.
- Không ghi DB live, không đổi mật khẩu / cấu hình / service: LIVE = BLOCKED.

Chi tiết: `01`–`08`.

## 1. Thống kê requirement

| Trạng thái | Số lượng | Requirement |
|---|---|---|
| PASS | 11 | SC-03, SC-04, CF-01, CF-02, CF-05, PR-01, PR-04, AR-02, AR-05, AR-07, EX-03 |
| PARTIAL | 12 | SC-02, CF-03, CF-04, PR-03, PR-05, PR-06, AR-01, AR-03, AR-04, EX-01, EX-02, EX-04 |
| MISSING | 2 | PR-02 (connector folder), AR-08 (thông báo KH qua email / messenger) |
| INCORRECT | 0 | — |
| NOT TESTED | 0 | — |
| PDF UNCLEAR | 3 | SC-01, HD-01, AR-06 |
| **Tổng** | **28** | Không tính 6 suy luận INF-xx và các đề xuất |

**Mẫu số** = tổng − PDF UNCLEAR = 28 − 3 = **25**.

| Chỉ số | Công thức | Kết quả |
|---|---|---|
| Tỷ lệ đạt đầy đủ | PASS / 25 = 11 / 25 | **44%** |
| Tỷ lệ có trọng số | (PASS + 0.5 × PARTIAL) / 25 = (11 + 6) / 25 | **68%** |

## 2. Luồng end-to-end

| Luồng | Trạng thái |
|---|---|
| F1 Tiếp nhận / OCR / trích xuất (upload) | TEST VERIFIED (phần upload; connector MISSING) |
| F2 Phân loại & cấu hình | TEST VERIFIED |
| F3 Phiếu thu / sao kê → KH → công nợ → ghi sổ | TEST VERIFIED |
| F4 Chiết khấu thanh toán HĐ | **BLOCKED theo thiết kế** (dừng ở `payment_discount_accounting_policy_missing`) |
| F5 Đề nghị thanh toán: kiểm tra → ghi nhận → chi | TEST VERIFIED |
| F6 Đề nghị thanh toán nhiều file | TEST VERIFIED |
| F7 Hợp đồng → lịch thanh toán | TEST VERIFIED |
| F8 Thông báo | MISSING |
| F9 Connector folder | MISSING |
| F10 Sổ cái / đối chiếu | MISSING |

**Tổng:** 6 / 10 luồng có bằng chứng TEST; 1 BLOCKED; 3 MISSING. **0 luồng LIVE VERIFIED**, vì mọi luồng live đều BLOCKED theo quy tắc không ghi dữ liệu và không đổi mật khẩu.

**Test đã chạy trong audit:**
- Gateway: 822 / 822 PASS, trên DB cô lập.
- Agent: 344 passed, 8 skipped.
- Web: tsc 0 lỗi; vitest 95 / 95.

## 3. Rủi ro chính

Không có phát hiện **CRITICAL** đã xác minh:
- không ghi nhận trùng;
- không lệch tenant;
- không chi tiền khi chưa duyệt;
- Agent không có thẩm quyền ghi sổ.

| ID | Rủi ro | Mức |
|---|---|---|
| R-01 / AG-01 | Chứng từ đã ghi sổ (phiếu thu, sao kê, hoá đơn) vẫn có thể bị trích xuất lại / `apply-extraction`, kể cả qua tool Agent ⇒ mất truy vết (không gây ghi sổ trùng) | HIGH |
| R-02 | Hoá đơn không sinh bút toán ⇒ không có sổ cái / cân đối / đối chiếu | HIGH |
| R-04 | Không có nghiệp vụ chi tạm ứng 141; cấn trừ tạm ứng toàn bộ | HIGH |
| XL-AR-03/04 | Chiết khấu thanh toán chưa ghi sổ được (thiếu chính sách TK) | HIGH |
| R-03, R-05, AG-02, AG-04, R-06, R-07 | Công nợ chưa trừ tiền chưa phân bổ; không đảo đề nghị thanh toán; OCR đồng thời; báo cáo Agent soạn; chính sách CK; thu thừa | MEDIUM |

R-08 (ADMIN không tách nhiệm) là **quyết định sản phẩm đã chấp nhận**, không tính là lỗi.

## 4. Quyết định nghiệp vụ cần có

1. **D-01:** Thông tin kế toán cần lấy từ HĐ; quy trình xử lý HĐ (tr.5 đang trống).
2. **D-02:** TK hạch toán chiết khấu thanh toán; CK khi thanh toán một phần; đảo CK.
3. **D-03:** So với "tổng công nợ" hay từng hoá đơn.
4. **D-04:** "Xử lý thừa": 131 trả trước / 3388 / hoàn tiền / cấn trừ; ngưỡng.
5. **D-05:** Kênh, người nhận, nội dung thông báo; nơi KH đăng ký kênh.
6. **D-06:** Quy trình công ty: lệnh công tác, yêu cầu mua hàng, tạm ứng.
7. **D-07:** Nguồn connector folder.
8. **D-08:** Nghĩa của "định dạng chứng từ".
9. **D-09:** Phạm vi sổ cái / báo cáo; hoá đơn có phải sinh bút toán (Nợ 131 / Có 511, 3331) không.

## 5. Phần chưa thể kiểm tra & lý do

| Phần | Lý do |
|---|---|
| Mọi luồng trên môi trường live (LIVE VERIFIED) | Cần đăng nhập và ghi dữ liệu; quy tắc audit cấm ghi DB live và cấm đổi mật khẩu |
| Tái hiện động R-01 / AG-01 | Không có test hiện hữu; viết test mới là thay đổi repo ngoài phạm vi audit. Kết luận dựa trên STATIC: không có guard tại `VoucherEndpoints.cs:1735-1781`, `IVoucherRepository.cs:431-455`, và không có trigger DB tương ứng |
| Lỗi OCR đồng thời của Agent (AG-02) | Cần tải OCR song song lên Agent đang chạy; không tái hiện trong audit |
| 8 test Agent bị skip | Cần môi trường / phụ thuộc đặc thù |
| Kênh thông báo ngoài, connector | Không tồn tại trong code nên không có gì để chạy |

## 6. Khuyến nghị thứ tự

Chi tiết trong `08`:
1. Chốt các quyết định D-01..D-09.
2. Khoá chứng từ đã ghi sổ (R-01).
3. Kế toán nền: bút toán hoá đơn, sổ cái, CK, thu thừa.
4. Tạm ứng và quy trình chi phí.
5. Thông báo và connector.

Audit này **không** triển khai thay đổi nào.
