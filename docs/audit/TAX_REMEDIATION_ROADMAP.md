# AIQ — Lộ trình khắc phục thuế (đề xuất, chờ phê duyệt)

Ngày: 2026-11-13. Thứ tự theo yêu cầu audit §10.I. Mã rủi ro: [TAX_RISK_REGISTER.md](TAX_RISK_REGISTER.md).
**Chưa có thay đổi mã nào.** Mỗi giai đoạn cần anh/chị phê duyệt trước khi bắt đầu.

## Nguyên tắc chung

- Không tạo module trùng: mở rộng `vat_rates` thành **rule store chung** thay vì tạo bảng rule thứ hai; engine tính thuế đặt ở **Gateway (C#, `decimal`)** cạnh `VoucherValidator`, để Gateway là nguồn tính toán có thẩm quyền; tool Agent gọi API Gateway thay vì giữ hằng số riêng.
- Mỗi rule: `effective_from`, `effective_to`, `legal_reference`, `version`, trạng thái duyệt (chuyên gia thuế xác nhận).
- Giữ nguyên ADMIN Department Authority Invariant. Mọi yêu cầu phân tách người lập/người nộp tờ khai phải có quyết định sản phẩm riêng.

## Giai đoạn 0 — Xác nhận pháp lý (chuyên gia thuế, không code)

Danh sách cần xác nhận: xem [TAX_WORKFLOW_AUDIT_REPORT.md §E](TAX_WORKFLOW_AUDIT_REPORT.md#e-legal-compliance-audit). Đầu ra: bảng tham số đã duyệt cho kỳ 2025 và 2026.

## Giai đoạn 1 — Lỗi sai công thức tính thuế (P0)

| Việc | Rủi ro | Kiểm chứng |
|---|---|---|
| Rule store chung có version (mở rộng `vat_rates` hoặc `tax_rules` thay thế + migrate dữ liệu) | H07 | Unit test chọn rule theo ngày |
| Engine TNCN: bộ rule 2025/2026, trần BH đúng căn cứ, `decimal`, làm tròn VND | C01, M02, M03 | PIT-01..07 xanh + test biên |
| Engine TNDN: thuế suất theo doanh thu kỳ trước, từ chối ưu đãi không hồ sơ | C02 | CIT-01..03 xanh + biên 3/50 tỷ |
| `calculate_*` Agent gọi engine Gateway; xoá hằng số Python | C01, C02, H07 | Test tool trả kết quả engine |

## Giai đoạn 2 — Lỗi sai quy định pháp luật (P0–P1)

| Việc | Rủi ro |
|---|---|
| Rule khấu trừ VAT (20tr/5tr theo ngày, cộng gộp trong ngày, chứng từ thanh toán thực tế, MST người mua, trạng thái xác thực) → ảnh hưởng `vat_deductible_amount` và Nợ 1331 | C03, M06 |
| Lịch nghĩa vụ kê khai một nguồn (Gateway): hạn cuối tháng, năm tài chính tenant, dời ngày nghỉ; bỏ logic trùng ở web; bỏ/sửa `02/TNDN`, `05/KK-TNCN` | H01 |
| Bổ sung cửa sổ 8% lịch sử; rà soát dòng 0/5/10/KCT theo Luật 48/2024 | M01 |
| Cập nhật skill `tax-calculation` theo rule đã duyệt; bỏ cấu hình 15% tiếp khách; sửa câu phạt | M07, L01, L02 |

## Giai đoạn 3 — Lỗi dữ liệu và đối chiếu (P1–P2)

| Việc | Rủi ro |
|---|---|
| Khoá chống trùng đúng (MST + mẫu số + ký hiệu + số chuẩn hoá), trùng → review thay vì tự từ chối | H02, L04 |
| Sổ/bảng kê VAT theo kỳ từ hoá đơn + bút toán posting; số chuyển kỳ | H04 |
| Quan hệ HĐ gốc ↔ điều chỉnh/thay thế/huỷ | H04 |
| Sổ cái / CĐPS từ posting làm nguồn cho TNDN | H06 |
| (Quyết định: tự xây hay tích hợp) dữ liệu payroll cho TNCN; nhập XML HĐĐT / MISA | H05 |

## Giai đoạn 4 — Phân quyền và tenant isolation (P1)

| Việc | Rủi ro |
|---|---|
| Đăng ký tool thuế trong migration (không chỉ seed demo); capability `accounting.tax` từ chối nếu agent thiếu tool tính thuế | M08 |
| Test security: cross-tenant/cross-department cho các bảng thuế mới | — |

## Giai đoạn 5 — Phê duyệt và audit trail (P1)

| Việc | Rủi ro |
|---|---|
| Bảng `tax_calculations` (input, output, rule_version, legal_refs, actor, tenant/department), bất biến; LLM chỉ giải thích từ bản ghi | H03 |
| Khi có tờ khai: trạng thái nháp → ADMIN duyệt → đã nộp (bất biến) → khai bổ sung = phiên bản mới; Agent không có tool duyệt/nộp | H04 |

## Giai đoạn 6 — Báo cáo và giao diện (P2)

| Việc | Rủi ro |
|---|---|
| Màn hình tính thuế gọi API engine trực tiếp, hiển thị kết quả có cấu trúc + căn cứ | H03, L03 |
| BCTC: lấy từ sổ cái, hoặc gắn nhãn "bản nháp LLM"; bỏ tên công ty hardcode | H06 |

## Giai đoạn 7 — Tính năng nâng cao (P3)

01/GTGT + XML + theo dõi tiếp nhận; module TNCN đầy đủ (khấu trừ, quyết toán, chứng từ khấu trừ, 05/QTT); TNDN tạm nộp, chuyển lỗ, ưu đãi, 03/TNDN; tích hợp MISA.
