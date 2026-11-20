# 11 — Kế hoạch demo AIQ (khách hàng & ban lãnh đạo)

Thời lượng: **40 phút** (35 phút trình diễn + 5 phút hỏi đáp; tối đa 45 phút). Kịch bản lời thoại: [12_DEMO_SCRIPT.md](12_DEMO_SCRIPT.md). Chuẩn bị dữ liệu: [13_DEMO_DATA_CHECKLIST.md](13_DEMO_DATA_CHECKLIST.md).

## 1. Nguyên tắc

1. Chỉ trình diễn chức năng đã xác minh trong đợt audit (PASS ở [01](01_SYSTEM_FUNCTIONAL_INVENTORY.md)). Chức năng UNVERIFIED chỉ được trình diễn **sau khi đã tổng duyệt thành công** trên đúng môi trường demo ([13 §3](13_DEMO_DATA_CHECKLIST.md)).
2. **Không trình diễn**: tính thuế TNCN/TNDN, hạn nộp tờ khai, báo cáo tài chính (B01/B02), trang Dashboard như bảng số liệu, trang Reports/Usage, DLP. Nếu khách hỏi, trả lời theo [12 §Hỏi đáp](12_DEMO_SCRIPT.md).
3. Thông điệp xuyên suốt: **AI đọc – kiểm tra – đề xuất; con người duyệt – ghi sổ; mọi thứ có dấu vết.**
4. Dữ liệu demo là dữ liệu mẫu; không dùng dữ liệu khách hàng thật.

## 2. Mức sẵn sàng theo phần

Ký hiệu trạng thái: ✅ đã xác minh live trong audit · 🟡 có test/mã nguồn, **bắt buộc tổng duyệt** · ⛔ không đưa vào demo.

| # | Phần | Phút | Tài khoản / vai trò | Trạng thái | Chức năng (ID ở 01) |
|---|---|---|---|---|---|
| 1 | Giới thiệu AIQ | 3 | — (slide `docs/slides/aiq-intro.pptx`) | ✅ | — |
| 2 | Đăng nhập & phân quyền | 4 | TENANT_OWNER → ADMIN → (USER) | ✅ | AUTH-01, AUTH-02, RBAC-01 |
| 3 | Quản lý Agent | 4 | ADMIN | ✅ xem · 🟡 publish | AGB-01..09, ASG-01 |
| 4 | Người dùng hỏi đáp với AI Agent | 5 | ADMIN (hoặc USER đã chuẩn bị) | ✅ | CHAT-01, AP-03, HITL-04, CTX-01 |
| 5 | Upload chứng từ | 3 | USER/ADMIN | 🟡 | VOU-01, OCR-03 |
| 6 | OCR & trích xuất | 3 | như trên | 🟡 (dự phòng: chứng từ đã xử lý sẵn ✅) | OCR-01, OCR-02 |
| 7 | AI kiểm tra & phân tích chứng từ | 3 | ADMIN | ✅ (trên chứng từ có sẵn) | ACC-05, AP-05, VOU-06 |
| 8 | Kiểm tra dữ liệu kế toán (công nợ, sao kê) | 3 | ADMIN | ✅ | AP-01, AP-03, AR-01, OCR-05 |
| 9 | Đề xuất nghiệp vụ (bút toán) | 2 | ADMIN | ✅ xem đề xuất có sẵn | ACC-01 |
| 10 | Người có thẩm quyền phê duyệt | 4 | ADMIN | 🟡 (đề nghị hoàn ứng / xác thực HĐ); ⛔ duyệt chứng từ v2 nếu chưa bật cờ | EXP-03, EXP-04, VOU-06 |
| 11 | Theo dõi trạng thái xử lý | 2 | ADMIN / TENANT_OWNER | ✅ | RPT-03, VOU-02 |
| 12 | Dashboard & báo cáo | 2 | ADMIN | ✅ chỉ công nợ + trạng thái chứng từ; ⛔ BCTC, Reports | AR-01, AP-03, RPT-03 |
| 13 | Audit log | 2 | ADMIN / TENANT_OWNER | ✅ nhật ký Agent + lịch sử chứng từ | AUD-02, AUD-03 |
| — | Hỏi đáp | 5 | — | — | — |

## 3. Chi tiết từng phần

| # | Dữ liệu cần chuẩn bị | Kết quả mong đợi | Ảnh dự phòng | Kiểm tra backend trước demo | Rủi ro | Phương án dự phòng |
|---|---|---|---|---|---|---|
| 1 | Slide giới thiệu | — | — | — | Slide có hứa tính năng chưa có | Rà slide theo [07](07_PRODUCT_GAP_ANALYSIS.md) |
| 2 | 3 tài khoản cùng tenant demo | TO → Dashboard lối tắt; ADMIN → vào thẳng Chat; TO mở `/vouchers` → "Không có quyền truy cập" | `tenant_owner-dashboard.png`, `admin-root.png`, `tenant_owner-vouchers.png` | `POST /auth/login` 200 cho cả 3 | Quên mật khẩu, hết hạn phiên | Đăng nhập sẵn ở 3 cửa sổ ẩn danh |
| 3 | Agent "Trợ lý kế toán" production, có tool dữ liệu | Thấy các tab, version đang publish, kill switch | `admin-department_agents_id.png` | `GET /department/agents` 200 | Bấm nhầm Publish/Disable khi demo | Chỉ trình bày; publish chỉ làm nếu đã tổng duyệt |
| 4 | Câu hỏi đã thử (12 §4) | Câu trả lời có số liệu thật; từ chối ghi sổ khi bị yêu cầu | `admin-root.png` + bản ghi câu trả lời C01, C02, C05 trong [evidence/chat-probes.json](evidence/chat-probes.json) | `GET /healthz/ready`; Agent không báo hết hạn mức | Hết hạn mức model ("session limit"), trả lời chậm 10–30 giây, câu gợi ý nha khoa trên màn hình | Chạy trước 1 câu "khởi động"; nếu hết hạn mức, chiếu bản ghi câu trả lời đã lưu |
| 5–6 | 1 hoá đơn GTGT **mới** (số chưa có trong DB), PDF có lớp chữ | Chứng từ chuyển "Đang OCR" → "Đã trích xuất" trong ~30–90 giây, nhận diện đúng loại | `admin-vouchers_upload.png` | Hàng đợi Hangfire chạy; `aiq-agent` RestartCount không tăng | **Trùng số HĐ → tự từ chối**; OCR chậm; hết hạn mức model | Dùng hoá đơn số mới; nếu lỗi, mở chứng từ đã xử lý sẵn (1C26TDP 0001041) |
| 7 | Hoá đơn có cờ rủi ro / lỗi validator (chuẩn bị riêng) | Thấy kiểm tra MST, tổng tiền, thuế suất, công nợ NCC, cờ rủi ro | `admin-voucher-invoice-in.png` | `GET /vouchers/{id}/validation` | Nhãn "Chi TM > 30M (vi phạm TT78)" dùng ngưỡng sai luật | Không nhấn vào nhãn này; nếu bị hỏi, nói thẳng là đang hiệu chỉnh |
| 8 | KH Minh An, NCC Đại Phát, sao kê có 2 giao dịch đã khớp | Công nợ, tuổi nợ, giao dịch MATCHED | `admin-customers_id.png`, `admin-suppliers_id.png`, `admin-voucher-bank-statement.png` | — | Dữ liệu `LIVE-TEST` lộ trên màn hình | Dọn/đổi tên dữ liệu thử trước demo |
| 9 | Chứng từ có bút toán đề xuất | Bút toán TT200 đề xuất, đánh dấu chưa ghi sổ / đã ghi sổ | `admin-voucher-invoice-in.png` | — | — | — |
| 10 | 1 đề nghị hoàn ứng ở trạng thái chờ duyệt phòng ban | ADMIN duyệt phòng ban → duyệt kế toán → chi tiền; bút toán POSTED | `admin-voucher-expense-claim.png` | Chạy `ExpenseClaimPaymentIntegrationTests`, `ExpenseClaimRecognitionIntegrationTests` trên DB thử (chưa chạy trong đợt audit) + tổng duyệt trên bản sao dữ liệu demo | Lỗi khi ghi sổ thật trước khách; hoá đơn chưa xác thực chặn chi tiền | Tổng duyệt hôm trước với bản sao dữ liệu; nếu lỗi, chuyển sang trình bày đề nghị đã chi (DNHU-2026-1017) |
| 11 | Chứng từ nhiều trạng thái | Báo cáo trạng thái chứng từ, bộ lọc danh sách | `admin-admin_voucher-status.png`, `admin-vouchers.png` | — | — | — |
| 12 | — | Công nợ khách hàng / NCC, trạng thái chứng từ | như 8, 11 | — | Khách yêu cầu xem Dashboard số liệu / BCTC | Nói rõ nằm trong lộ trình ([15](15_RECOMMENDED_ROADMAP.md)) |
| 13 | Đã có lượt chat trong ngày | Nhật ký Agent theo phòng; lịch sử sự kiện của chứng từ | `admin-department_audit.png` | Agent `/admin/audit` trả dữ liệu | Khách hỏi nhật ký thao tác người dùng | Nói rõ: có ghi nhận ở máy chủ, màn hình tra cứu nằm trong lộ trình |

## 4. Những gì không được nói là "đã hoàn chỉnh"

Tính thuế TNCN/TNDN, lịch hạn nộp tờ khai, kê khai thuế, báo cáo tài chính, sổ cái, Dashboard số liệu, báo cáo usage, DLP/che dữ liệu cá nhân, tích hợp MISA/ERP, đọc XML hoá đơn điện tử, thông báo email/Zalo, quy trình duyệt nhiều cấp cấu hình được, RAG trên tài liệu nội bộ (chưa kiểm tra live).

## 5. Demo-ready ≠ Production-ready

Đạt checklist [13](13_DEMO_DATA_CHECKLIST.md) nghĩa là **đủ điều kiện demo có kiểm soát**, không có nghĩa là sẵn sàng vận hành thật. Điều kiện production: [14_PRODUCT_READINESS.md](14_PRODUCT_READINESS.md).
