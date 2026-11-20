# 14. Phase 0: trạng thái triển khai

Cập nhật: 2026-11-17, sau đợt 7 (hết kế hoạch Phase 0, 01 §4). Chạy local; **chưa deploy, chưa chạy trên database thật.** Mọi test đều chạy trên PostgreSQL tạm (`aiq-test-pg`).

| Hạng mục | Trạng thái | Tài liệu |
|---|---|---|
| Đợt 1: khoá JWT, tắt tra cứu TCT, nhãn AI, banner tính năng | Xong, đã test | 02, 03 |
| Đợt 2: kho quy tắc thuế theo phòng ban (072), resolver | Xong, đã test | 07–11 |
| Đợt 3: tính thuế tất định, provenance (073), validator v4, cờ D2 | Xong, đã test | 12 |
| Hardening A (N1): không có số khi thiếu quy tắc làm tròn | Xong, đã test (pure + DB + E2E) | 02, 12, 13 |
| Hardening B (N2): catalog làm tròn | Đã thay bằng Batch 4A | 13 |
| **Batch 4A**: catalog làm tròn (074), engine, tích hợp tính thuế | **PASS**, đã test (2056 / 426 / 121). Mọi giá trị là BLOCKED_PENDING_LEGAL_CONFIRMATION; `CIT.ROUNDING` inactive | 13, 15 |
| Hardening C (N4): D2 với dữ liệu thiếu, âm, không khớp | Thay bằng Batch 4B | 04 (T6–T8) |
| **Batch 4B**: D2 theo H-1 / H-2 / H-3 (trạng thái, mã lý do, bổ sung có audit) | **PASS** kỹ thuật (2117 / 432 / 128; E2E 34 + 7 + 17). Không migration mới | 16 |
| **Batch 4B, sửa quyền đóng mục D2** | **PASS**: chỉ ADMIN phòng ban sở hữu (2173 / 436 / 130; E2E 71 + 12 + 27). Không migration | 18 |
| **Batch 4B rà soát cuối**: Q-1…Q-6, `payment_method` cũ | Q-2, Q-3 **PASS**; Q-1, Q-4, Q-5, Q-6, §7 **PARTIAL** (BLOCKED B-1…B-6). Đã test (2162 / 436 / 130; E2E 50 + 12 + 25). Không migration mới. **Chưa sẵn sàng production** | 17 |
| Hardening D (N7): danh tính agent, phạm vi theo principal | Xong, đã test (integration + E2E với Gateway thật) | 04 (S7) |
| Hardening E: concurrency trên hai kết nối thật | Xong, 6 kịch bản | 03 |
| Hardening F: E2E Web / Agent / job extract / provenance | Xong: Gateway thật + `next dev` + tool Agent thật; job extract chạy với OCR/LLM giả lập | 03 |
| Hardening G (T5): nhãn ngưỡng | Xong, không đổi giá trị | 04 |
| **Đợt 4**: hạn nộp theo quy tắc `TAX_ADMIN.DEADLINE.*` đã duyệt (P0-09, T4) | **PASS** (2187 / 440 / 135; E2E 81 + 15 + 33). Không migration. Chưa có lịch nghỉ lễ | 19 |
| **Đợt 5**: hoá đơn trùng (P0-05, D3 / D4, S3), migration 075 | **PASS** (2222 / 440 / 137; E2E 100 + 15 + 38). Cần preflight trên bản sao + cửa sổ bảo trì | 20 |
| **Đợt 6**: `audit_log` chỉ cho thêm dòng + phạm vi request (P0-08, S4), migration 076 | **PASS** (2226; E2E 106 + 15 + 38). Không chống superuser / chủ bảng | 21 |
| **Mục không chủ + ghi chú bắt buộc**: ghi chú bắt buộc cho mọi loại mục; script đối soát mục không chủ (chỉ đọc) | Ghi chú **PASS**, phân quyền **PASS** (2239; E2E 129 + 15 + 40; isolation 228 / 228). Mục không chủ: **BLOCKED** (cần bản sao production) | 24 |
| **Hoàn tất phân quyền**: hàng đợi duyệt theo phòng ban sở hữu cho mọi loại mục (đóng 18 R-1 / R-2) | **PASS** (2234; E2E 125 + 15 + 39; isolation 228 / 228, không KNOWN). BLOCKED B-1: mục của chứng từ không có phòng ban | 23 |
| **Đợt 7**: cô lập tenant / phòng ban (P0-06): ma trận HTTP 55 route × 4 actor, rà 84 câu SQL, test chống tái phát | **PASS** (2229; isolation 226 / 226). Sửa 2 lỗi 500 (export, thiếu tenant) | 22 |
| **Rà soát phát hành / kiểm tra cuối / kế hoạch khắc phục** | NO-GO production (25, 26). Quyết định A-1 / A-2 / A-3, Q-1…Q-9, K-1…K-3 đã chốt (27 bản 3) | 25, 26, 27 |
| **Bước 1**: số liệu bản sao production (chỉ đọc) | Script `sql/28_step1_baseline_counts.sql` sẵn sàng, đã kiểm trên DB tạm. **NO-GO về số liệu**: chưa có bản sao | 28 |
| **A-3**: mọi quy tắc đầu vào VAT / TNCN phải còn căn cứ pháp lý (S1–S7, Q-7, Q-8) | **GO kỹ thuật** (2291; Agent 444; Web 142; E2E 141 + 15 + 40; isolation 228 / 228). Không migration. Nghiệm thu và đóng băng phạm vi (30) | 29, 30 |
| **A-1** (cùng PR5): mục duyệt có `status IS DISTINCT FROM 'approved'` chặn P1–P11 + pay, kiểm trong transaction ghi sổ, khoá Voucher → Approval Queue; đảo / gỡ bắt buộc lý do (Q-9) | **GO kỹ thuật** (2376; A-1 85; Agent 444; Web 145; E2E 164 + 15 + 43; isolation 228 / 228; race 20 vòng × 2 kết nối; 2 mutation test). Không migration, không đổi RBAC. A1-F1 / A1-F2 đã quyết và đã làm (32): Agent bị chặn trên 19 route ghi sổ / ghi nhận / đảo / gỡ; ghi nhận hoàn ứng qua cổng (2399; E2E 174 + 17 + 43; isolation 228). A1-R7 đã làm (33): thêm 9 route quyết định, tổng 28 route chặn Agent; rà 256 route ghi, test quét route bắt buộc phân loại (2405; E2E 187 + 17 + 43; isolation 228). **Cần quyết định A1-R8…R11**: 97 quyết định Agent còn gọi được (khoá / mở kỳ, `/tenant/users/*`, publish Agent / workflow, đổi mật khẩu, `submit`) | 31, 32, 33 |

## Chờ quyết định

| # | Câu hỏi | Ở đâu |
|---|---|---|
| A-1…A-5 | Giả định của Batch 4A (ánh xạ dòng TNCN, phạm vi VAT `general`, cơ sở ngày, `none`, thứ tự TNCN) | 13 §6 |
| Giá trị làm tròn | Phương pháp, đơn vị, thời điểm của từng phòng ban: chuyên gia xác nhận, ADMIN duyệt | 15 §6 |
| B-1 | Đã giải quyết (đóng mục D2) | 18 |
| R-1, R-2 | **Đã đóng ở 23** (mọi mục theo phòng ban sở hữu) | 23 |
| 23 B-1 → 24 | Mục không chủ: chạy `sql/24_orphan_approval_audit.sql` trên bản sao, quyết định từng nhóm (không tự gán phòng ban) | 24 §2 |
| 23 B-2 | Duyệt liên phòng ban: tính năng riêng, ngoài Phase 0 (đã chốt) | 24 |
| B-2 | Chặn ghi sổ khi đang có xung đột OCR / người dùng (cần migration) | 17 §8 |
| B-3 | Số liệu `payment_method` trên bản sao production | 17 §6 |
| B-4, B-5 | Đối chiếu khi có chiết khấu; thanh toán hỗn hợp (BLOCKED_PENDING_LEGAL_CONFIRMATION) | 17 §8 |
| B-6 | USER giải quyết xung đột dữ liệu của chứng từ mình | 17 §8 |
| D2 BLOCKED | Nhiều phương thức trên một chứng từ; ngưỡng không theo từng hoá đơn; giá trị ngưỡng từng phòng ban | 16 §11 |
| N3 | Cách tính TNDN | 12 §11 |
| DL-1, DL-2 | Lịch nghỉ lễ cho hạn nộp; năm tài chính khác năm dương lịch | 19 §5 |
| AU-1 | Tách quyền DB để superuser / chủ bảng không tắt được trigger audit | 21 §5 |

## Điều kiện production chưa đạt
Xem 05 (đợt 3, R7, và mục hardening):

- quy tắc đã duyệt cho từng phòng ban;
- chạy thử 072 / 073 trên bản sao production;
- backup;
- phương án chuyển đổi chứng từ;
- chạy thử 074 trên bản sao production; quy tắc làm tròn pháp lý được duyệt ở từng phòng ban, nếu muốn có số thuế;
- chạy `sql/24_orphan_approval_audit.sql` trên bản sao production và xử lý các mục không chủ (24 §2); báo trước cho người dùng về quyền duyệt theo phòng ban và ghi chú bắt buộc;
- chạy `sql/17_legacy_payment_method_audit.sql` trên bản sao production và đối soát truy vấn 5–6 (17 §6);
- chạy `sql/20_duplicate_preflight.sql` trên bản sao trước migration 075; xử lý tay các nhóm xung đột; cửa sổ bảo trì (20 §6).
- rà mọi script vận hành có UPDATE / DELETE / TRUNCATE `audit_log` trước migration 076 (21, 05);
- chạy thử 075 / 076 trên bản sao production.
