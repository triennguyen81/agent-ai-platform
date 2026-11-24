# 14 — Mức sẵn sàng sản phẩm

Ngày: 2026-11-19. Căn cứ: [01](01_SYSTEM_FUNCTIONAL_INVENTORY.md)–[07](07_PRODUCT_GAP_ANALYSIS.md). Thang đánh giá: **Sẵn sàng** · **Sẵn sàng có điều kiện** · **Chưa sẵn sàng**. Điểm % là đánh giá định tính của audit, không phải đo tự động.

> **Demo Ready ≠ Production Ready.** AIQ có thể demo có kiểm soát sau khi hoàn thành các việc P0; AIQ **chưa** sẵn sàng vận hành với dữ liệu kế toán thật.

## 1. Tám trục đánh giá

| Trục | Mức | % | Căn cứ chính | Điều kiện để lên mức tiếp theo |
|---|---|---|---|---|
| 1. Technical | Sẵn sàng có điều kiện | 70 | Build sạch; 1.211 + 411 + 107 test xanh; health check ok; kiến trúc manifest/policy rõ ràng | Chạy 494 integration test trên DB riêng; sửa Agent metrics (G-21), OpenAPI 500, chế độ Development (G-16) |
| 2. Functional | Sẵn sàng có điều kiện | 55 | 74/174 PASS; lõi chứng từ – công nợ – chi phí – hợp đồng hoạt động | Sửa 7 BROKEN; xác minh 49 UNVERIFIED; bật quy trình chứng từ v2 (G-13) |
| 3. Security | Chưa sẵn sàng (production) / đủ cho demo nội bộ | 60 | Cô lập tenant 4/4, RBAC 8/9 đúng; SSRF tốt | Tắt đăng ký công khai (G-05), bật DLP + che PII trong chat (G-06, G-07), đóng cổng hạ tầng (G-08), sửa hàng đợi L2 (G-04) |
| 4. Accounting compliance | **Chưa sẵn sàng** | 35 | AI không tự ghi sổ (PASS); posting bất biến; nhưng thuế TNCN/TNDN/hạn nộp sai, khấu trừ VAT sai ngưỡng, không sổ cái, BCTC do AI soạn | G-01, G-02, G-03, G-09, G-11 + chuyên gia thuế xác nhận |
| 5. User experience | Sẵn sàng có điều kiện | 55 | Đăng nhập vào thẳng chat; luồng chứng từ rõ ràng | Dashboard có số liệu, trang báo cáo thật (G-14); bỏ câu gợi ý nha khoa (G-30); thông báo lỗi rõ khi thiếu quyền/tenant (G-35) |
| 6. Documentation | Sẵn sàng có điều kiện | 60 | Bộ tài liệu này (hướng dẫn 3 nhóm người dùng, demo, audit) | Cập nhật sau mỗi đợt sửa; bổ sung hướng dẫn cài đặt/vận hành |
| 7. Demo | **Sẵn sàng có điều kiện** | 65 | 9/13 phần demo đã xác minh live; phần còn lại có dự phòng | Hoàn thành P0 ([15](15_RECOMMENDED_ROADMAP.md)) + tổng duyệt |
| 8. Production | **Chưa sẵn sàng** | 30 | 3 CRITICAL, 12 HIGH chưa xử lý | Hoàn thành P0–P2 + kiểm thử bảo mật + quy trình sao lưu/khôi phục |

## 2. Phân loại vấn đề

| Mức | Số | Mã ([07](07_PRODUCT_GAP_ANALYSIS.md)) |
|---|---|---|
| **Critical blockers** (chặn nghiệm thu nghiệp vụ thuế & production) | 3 | G-01 TNCN · G-02 TNDN · G-03 khấu trừ VAT |
| **High priority** | 12 | G-04 hàng đợi L2 · G-05 đăng ký công khai · G-06 DLP tắt · G-07 chat bỏ qua DLP · G-08 cổng mở · G-09 hạn nộp · G-10 dò trùng · G-11 sổ cái/BCTC · G-12 hồ sơ tính toán AI · G-13 quy trình v2 tắt · G-14 dashboard/báo cáo · G-15 kê khai/lương |
| **Medium** | 14 | G-16 … G-29 |
| **Low** | 7 | G-30 … G-36 |

### Chặn demo (phải xử lý hoặc né trong kịch bản)
G-13 (quy trình duyệt chứng từ tắt) · G-22 (agent thử nghiệm là agent mặc định) · G-28 (dữ liệu LIVE-TEST lộ) · G-10 (tải lại hoá đơn cũ bị từ chối) · thiếu USER demo trong tenant Sáu Trực · G-01/G-02/G-09 (không trình diễn thuế).

### Chặn production
Toàn bộ CRITICAL + HIGH, cộng G-16, G-17, G-18 (cấu hình bảo mật triển khai).

## 3. Kết luận

| Mục tiêu | Kết luận |
|---|---|
| Demo cho khách hàng / ban lãnh đạo | **Được**, theo kịch bản [12](12_DEMO_SCRIPT.md), sau khi hoàn thành P0 và tổng duyệt; không trình diễn thuế, BCTC, dashboard số liệu |
| Nghiệm thu (UAT) | **Chưa** — cần P1 (thuế đúng luật, quyền duyệt, dò trùng, báo cáo, audit) |
| Production với dữ liệu thật | **Chưa** — cần P1 + P2 |
