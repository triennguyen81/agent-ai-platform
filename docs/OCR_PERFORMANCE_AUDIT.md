# OCR PERFORMANCE AUDIT — AIQ

**Ngày:** 2026-10-15

**Trạng thái:** đã đo, đã tối ưu trong code (working tree), **chưa deploy**.

**Quy ước số liệu**

| Nhãn | Nghĩa |
|---|---|
| ĐO | Đo thật trên code và model thật |
| PROD | Telemetry production (log Gateway, transcript CLI), một lần chạy, chỉ đọc |
| CHƯA ĐO | Không có số đo; nêu lý do |

**Phương pháp đo**

- **Môi trường:** container cô lập dựng từ image Agent, không truy cập Gateway / DB. Chạy offline với `--network none`; chạy có LLM thì chỉ ra internet tới Anthropic.
- **"Trước":** chạy chính **code đang deploy** (package trong image, build 2026-10-07).
- **"Sau":** chạy **working tree**.
- **Dữ liệu:** chứng từ `demo-output/samples/`.
- **Số lần:** 3 lần mỗi loại; lần 1 tính là cold, các lần sau là warm, báo cáo **trung vị warm**.
- **Gọi Claude thật:** được người dùng cho phép — 54 lượt (33 trước, 21 sau). Token lấy từ log phiên của Claude CLI, chỉ đọc số.
- **Script:** `agent/scripts/ocr_benchmark.py`.
- **Số liệu thô:** `docs/ocr-benchmark/*.json` (chỉ số, không có nội dung chứng từ).

## 1. Nguyên nhân gốc (có bằng chứng)

| # | Nguyên nhân | Bằng chứng |
|---|---|---|
| R1 | **Bước "sửa dấu OCR" bằng LLM chiếm phần lớn thời gian.** Mỗi trang là một phiên Claude CLI (`claude-sonnet-4-6`). Khi mô hình tự bật suy luận (adaptive thinking), một trang sinh **4.000–9.200 output token** dù văn bản trả về chỉ dài bằng đầu vào (~1.000 ký tự) | PROD 12/10: sao kê 78,9 giây / 7.466 output token, đề nghị thanh toán 86,4 giây / 7.024, hoá đơn 12,7 giây / 822 (không thinking). ĐO trước: sửa dấu 14–101 giây / trang (trung vị) |
| R2 | **PDF có text layer vẫn bị render + OCR + sửa dấu.** Đường voucher và hoàn ứng (`/ocr/pdf`, `ocr/pipeline.py::run_pdf`) luôn render; chỉ đường hợp đồng (`/documents/extract`) dùng text layer | Code: `run_pdf` → `render_pdf_pages` → OCR → correct cho mọi trang. Cả 7 PDF mẫu đều có text layer chuẩn (PDFium trả NFC đúng) |
| R3 | OCR thô kém với tiếng Việt: model PP-OCRv4 (RapidOCR) bỏ dấu, nên word recall OCR thô chỉ **0,16–0,36**. Bước sửa dấu tồn tại vì lý do này | ĐO offline |
| R4 | Prompt mỗi lượt sửa dấu ~15.400 token (định nghĩa built-in tool của CLI); prompt trích xuất ~26.500 token (9 skill nhúng toàn văn) | PROD transcript; ĐO trước |
| R5 | OCR được tuần tự hoá (OcrGate / AG-02), nên tài liệu thứ hai phải chờ trọn R1 của tài liệu đầu | PROD: hoá đơn hoàn ứng thứ 2 xong sau 87,6 giây, trong đó 28,8 giây là chờ |

**Không phải nguyên nhân:**

- **Khởi tạo model RapidOCR:** 3,1–3,4 giây, một lần cho mỗi tiến trình; Agent production giữ model đã load.
- **Render PDFium:** 0,14–0,17 giây / trang.
- **LLM trích xuất:** 5–22 giây.
- **Validation phía Gateway:** extract job trừ phiên LLM còn ~1,5 giây.
- **Thiếu RAM / swap:** host 8 vCPU, 15,5 GB RAM, còn ~5,5 GB; swap đã dùng 2,7 GB, có từ trước.

## 2–4. Thời gian trước / sau và mức cải thiện

### 2.1 Từng giai đoạn — PDF số, không LLM (ĐO offline, trung vị warm, 200 dpi)

| Chứng từ | Trang | Kích thước | Trước: render | Trước: OCR | Trước: CPU | Sau: text layer | Sau: CPU |
|---|---|---|---|---|---|---|---|
| Hoá đơn GTGT | 1 | 50 KB | 0,146 s | 5,54 s | 21,3 s | **0,002 s** | 0,00 s |
| Sao kê | 1 | 49 KB | 0,143 s | 5,83 s | 22,3 s | 0,002 s | 0,00 s |
| Đề nghị thanh toán | 1 | 49 KB | 0,138 s | 4,81 s | 18,4 s | 0,002 s | 0,00 s |
| Hoá đơn chi phí | 1 | 50 KB | 0,142 s | 5,45 s | 20,9 s | 0,002 s | 0,00 s |
| Hợp đồng | 2 | 53 KB | 0,287 s | 11,51 s | 44,2 s | 0,003 s | 0,00 s |

Khởi tạo RapidOCR (cold): 3,19 s / 3,9 CPU-s, RSS sau khi khởi tạo 191 MB.

### 2.2 End-to-end gồm LLM (ĐO, trung vị warm của 3 lần)

E2E = OCR + sửa dấu + trích xuất.

| Chứng từ (PDF số) | Trước: OCR + sửa dấu | Trước: trích xuất | **Trước: E2E** | Sau: OCR + sửa dấu | Sau: trích xuất | **Sau: E2E** | Cải thiện |
|---|---|---|---|---|---|---|---|
| Hoá đơn GTGT | 20,2 s | 11,1 s | **30,8 s** | 0,00 s | 7,1 s | **6,7 s** | **×4,6 (−78%)** |
| Sao kê ngân hàng | 124,2 s | 22,0 s | **145,4 s** | 0,00 s | 17,2 s | **17,4 s** | **×8,4 (−88%)** |
| Đề nghị thanh toán | 49,7 s | 7,6 s | **56,9 s** | 0,00 s | 6,4 s | **6,3 s** | **×9,0 (−89%)** |
| Hoá đơn chi phí (hoàn ứng) | 72,4 s | 7,6 s | **79,3 s** | 0,00 s | 6,3 s | **6,4 s** | **×12,4 (−92%)** |

Cold (lần 1) trước / sau:

| Chứng từ | Trước (OCR + sửa dấu, cold) | Sau |
|---|---|---|
| Hoá đơn | 22,1 s | 0,01 s |
| Sao kê | 69,6 s | 0,00 s |
| Đề nghị thanh toán | 64,2 s | 0,00 s |
| Hoá đơn chi phí | 86,6 s | 0,00 s |

Thời gian phiên LLM dao động mạnh giữa các lần, vì mô hình tự quyết có suy luận hay không. Ví dụ hoá đơn chi phí "trước" có lần 86,6 s, có lần 72 s.

| Chứng từ ảnh scan (JPEG) | Trước: OCR + sửa dấu | Sau | Cải thiện |
|---|---|---|---|
| Hoá đơn | 100,2 s | **21,7 s** | ×4,6 |
| Sao kê | 138,5 s | **19,7 s** | ×7,0 |
| Đề nghị thanh toán | 71,3 s | **16,8 s** | ×4,2 |

Với ảnh scan vẫn phải OCR (~5–6 s) và sửa dấu; phần tăng tốc đến từ việc tắt thinking.

**So sánh với production 12/10 (PROD, code trước):** OCR job hoá đơn 22,9 s, sao kê 88,3 s, đề nghị thanh toán 93,2 s, cùng bậc với số đo "trước". Lưu ý số đo production là một lần chạy duy nhất.

### 2.3 Số lần gọi LLM và token

ĐO, trung vị mỗi tài liệu.

| | Trước | Sau |
|---|---|---|
| Lượt sửa dấu / PDF số | 1 / trang | **0** |
| Output token sửa dấu / trang | 822–9.194 (trung vị theo loại 822–7.827) | 0 (PDF số); 529–820 (ảnh scan, thinking tắt) |
| Prompt token sửa dấu / trang | **15.235–15.697** (ĐO, 21 lượt) | 0 (PDF số); **712–925** (ảnh scan, 9 lượt: `tools=[]` + không thinking) |
| Prompt token trích xuất | 26.344–26.658 | **7.082–7.336** |
| Output token trích xuất | 906–3.239 | 691–2.427 |
| Lượt trích xuất / tài liệu | 1 | 1 |
| Tổng lượt trong benchmark | 33 (21 sửa dấu + 12 trích xuất) | 21 (9 sửa dấu ảnh scan + 12 trích xuất) |

## 5. So sánh độ chính xác (ĐO)

| Chứng từ | Word recall văn bản: trước → sau | CER văn bản: trước → sau | Loại chứng từ AI xác định: trước → sau | Golden recall trích xuất: trước → sau |
|---|---|---|---|---|
| Hoá đơn (PDF) | 0,93 → **1,00** | 0,25 → **0,00** | 3/3 → 3/3 | 1,00 → 1,00 |
| Sao kê (PDF) | 0,63 → **1,00** | 0,38 → **0,00** | 3/3 → 3/3 | 0,86 → 0,86 |
| Đề nghị thanh toán (PDF) | 0,87 → **1,00** | 0,43 → **0,00** | 3/3 → 3/3 | 1,00 → 1,00 |
| Hoá đơn chi phí (PDF) | 0,92 → **1,00** | 0,27 → **0,00** | 3/3 → 3/3 | 1,00 → 1,00 |
| Hợp đồng (PDF, chỉ văn bản) | 0,20 (OCR thô) → **1,00** | 0,38 → **0,00** | — | — |
| Hoá đơn (ảnh scan) | 0,92 → 0,93 | 0,25 → 0,25 | — | — |
| Đề nghị thanh toán (ảnh scan) | 0,86 → 0,86 | 0,43 → 0,43 | — | — |
| **Sao kê (ảnh scan)** | **0,79 → 0,70** | 0,34 → 0,33 | — | — |

Cách tính:
- **Văn bản chuẩn:** text layer của chính PDF — đúng nội dung đã in.
- **Word recall:** không phụ thuộc thứ tự đọc.
- **CER:** phụ thuộc thứ tự đọc, nên bị phóng đại với bảng.
- **Golden recall:** tỷ lệ giá trị vàng (số hoá đơn, ngày, tiền, MST…) có trong JSON trích xuất.

Nhận xét:
- Với PDF số, văn bản đầu vào cho bước trích xuất trở nên **chính xác tuyệt đối**. Độ chính xác trích xuất giữ nguyên trên bộ mẫu.
- Riêng ảnh scan sao kê, word recall sau khi tắt thinking **giảm 0,79 → 0,70**, dù CER không đổi. Đây là rủi ro thật, xem §9.
- Độ chính xác **trích xuất** trên ảnh scan chưa được đo (benchmark chỉ trích xuất trên PDF).
- DPI 150 không được áp dụng: nhanh hơn ~20% và RSS 727 MB thay vì 1.072 MB, nhưng word recall OCR thô của ảnh scan giảm (hoá đơn 0,16 → 0,09; đề nghị thanh toán 0,14 → 0,06). Giữ 200 dpi.

## 6. CPU / RAM peak (ĐO)

| | Trước | Sau |
|---|---|---|
| CPU cho OCR / trang (PDF số) | 18–22 CPU-giây (gần bão hoà 4 lõi trong ~5 s) | ~0 (text layer: 2–3 ms) |
| CPU cho OCR / trang (ảnh scan) | 18–24 CPU-giây | như trước |
| RSS tiến trình benchmark (peak) | 1.029–1.072 MB | 1.074–1.092 MB |

Ghi chú:
- RSS peak "sau" không giảm vì benchmark vẫn chạy các trang ảnh scan (RapidOCR) trong cùng tiến trình. Với tài liệu PDF số, đường mới không chạy RapidOCR nên không phát sinh phần bộ nhớ inference đó (~800 MB trên mức sau khởi tạo).
- RSS của phiên Claude CLI con: số đo từ `RUSAGE_CHILDREN` trùng giá trị với tiến trình chính, nên không coi là số đo riêng tin cậy.

## 7. Thay đổi code (working tree)

| File | Thay đổi |
|---|---|
| `agent/src/aiq_agent/ocr/pdf.py` | `usable_text_layer()`: đủ dài, không ký tự hỏng / điều khiển, đa số chữ-số. `analyse_pdf_pages()`: **một lượt PDFium dưới `PDFIUM_LOCK`** đọc text layer và tỷ lệ ảnh phủ trang; **chỉ render trang cần OCR**. Trang có ảnh phủ ≥ 50% luôn bị coi là scan và OCR, kể cả khi có text overlay |
| `agent/src/aiq_agent/ocr/pipeline.py` | `text_layer_first`: trang dùng text layer giữ nguyên văn (confidence 1,0, `source="text_layer"`, không gọi LLM); trang còn lại OCR + sửa dấu như cũ; validation chạy trên toàn bộ tài liệu, giữ đúng số trang |
| `agent/src/aiq_agent/ocr/extractor.py` | `OcrPage.source` |
| `agent/src/aiq_agent/api/ocr_routes.py` | `PageReport.source` (tuỳ chọn; Gateway bỏ qua trường lạ) |
| `agent/src/aiq_agent/llm/claude_cli.py` | `disable_thinking` → `thinking={"type": "disabled"}`, kèm `tools=[]` (đã có trong working tree) |
| `agent/src/aiq_agent/main.py` | Client LLM riêng cho sửa dấu, không thinking, `max_turns=1`; chat giữ mặc định. Bật `text_layer_first` |
| `agent/src/aiq_agent/config.py` | `OCR_PDF_TEXT_LAYER_FIRST=true`, `OCR_CORRECTION_DISABLE_THINKING=true` |
| `agent/scripts/ocr_benchmark.py` | Benchmark từng giai đoạn: CPU, RSS, token, CER, word recall, golden recall |
| `agent/tests/test_ocr_text_layer.py` | 9 test với **PDFium thật** |

**Giữ nguyên:**
- Validation, cổng review, kiểm tra tenant (Gateway không đổi).
- Lock PDFium và hàng đợi OCR của AG-02: PDFium vẫn chỉ một luồng.
- DPI 200.
- Không đổi hợp đồng API giữa Gateway và Agent: chỉ thêm trường tuỳ chọn.

Phần tinh gọn prompt trích xuất (26,5k → 7,2k token) và `tools=[]` là thay đổi đã làm ở đợt tối ưu token. Chúng được đo lại ở đây vì nằm trên cùng đường xử lý.

**Không triển khai (kèm lý do):**

| Đề xuất | Lý do không làm |
|---|---|
| Cache kết quả OCR | Thử lại hoàn ứng (item retry) và trích xuất lại voucher (reextract) đã dùng lại văn bản OCR đã lưu. Cache theo hash file chỉ có ích khi upload trùng file — chưa có số liệu cho thấy cần, nên không thêm trạng thái mới cần kiểm soát tenant |
| OCR theo vùng | Chưa có bằng chứng cần |
| Gộp nhiều trang / lượt sửa dấu | Đã có từ đợt trước, đang tắt |

## 8. Kết quả kiểm thử

| Bộ | Kết quả |
|---|---|
| Agent `pytest` toàn bộ (image Agent, `--network none`) | **403 passed, 8 skipped** (thêm 9 test) |
| Test mới với PDFium thật | Trang số dùng text layer và không render; ảnh scan luôn OCR; **scan có text overlay vẫn OCR**; PDF trộn chỉ OCR trang scan, giữ thứ tự 1-2-3; PDF số 100% không gọi LLM; tắt cờ trở về đường cũ; client sửa dấu gửi `thinking: disabled`, client chat giữ mặc định |
| Hợp đồng | Đường `/documents/extract` không đổi: đã dùng text layer, ĐO 0,003 s cho 2 trang. Thêm vào đường `/ocr/pdf`: 2 trang text layer, word recall 1,00 |
| Hoá đơn / sao kê / hoàn ứng / đề nghị thanh toán | ĐO ở §2–5: 3/3 xác định đúng loại, golden recall không giảm |
| Crash / ổn định | 57 lượt chạy benchmark (offline + LLM), không lỗi, không crash. PDFium vẫn dưới lock (AG-02) |
| Production | **Không** ghi dữ liệu, **không** đổi cấu hình hay deploy. Chỉ đọc log Gateway và đọc số liệu từ transcript CLI. File token tạm của benchmark đã xoá |

**CHƯA ĐO:**

| Hạng mục | Lý do |
|---|---|
| Thời gian upload qua Gateway (S3 + DB) | Muốn đo phải ghi dữ liệu production. Đường upload không đổi |
| Độ chính xác trích xuất trên ảnh scan | Benchmark chỉ trích xuất trên PDF |
| Hiệu quả trên chứng từ thật của khách hàng | Không dùng dữ liệu thật |

## 9. Rủi ro còn lại

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| Tắt thinking khi sửa dấu: ảnh scan sao kê có word recall 0,79 → 0,70 (trung vị 3 lần) | TRUNG BÌNH | Chỉ ảnh hưởng ảnh scan; PDF số không qua bước này. Đặt `OCR_CORRECTION_DISABLE_THINKING=false` nếu ưu tiên chính xác hơn tốc độ cho ảnh scan. Nên đo thêm độ chính xác **trích xuất** trên ảnh scan thật trước khi bật trên production |
| PDF có text layer sai (font thiếu ToUnicode, mã hoá lỗi, text layer OCR kém của máy scan) | TRUNG BÌNH | `usable_text_layer` loại ký tự hỏng / điều khiển / ít chữ-số; trang là ảnh scan (ảnh phủ ≥ 50%) luôn OCR. Không bắt được text layer "trông hợp lệ nhưng sai nội dung". Rollback: `OCR_PDF_TEXT_LAYER_FIRST=false` |
| Văn bản text layer giữ nguyên dòng watermark / header / footer của PDF | THẤP | Giống hành vi OCR cũ (OCR cũng đọc các dòng đó) |
| Ảnh scan vẫn mất ~17–22 s mỗi trang | THẤP / MEDIUM | Nút thắt còn lại là OCR CPU (~5–6 s) + sửa dấu (~11–14 s). Tuỳ chọn tiếp: gộp trang (đã có cờ) hoặc dùng model nhỏ hơn cho sửa dấu — cần đo chất lượng |
| OCR vẫn tuần tự hoá (AG-02) | THẤP | Với PDF số, OCR gần như 0 nên hàng đợi trống nhanh |
| Thay đổi chưa deploy; production vẫn chạy code 10-07 | — | Cần phê duyệt deploy. Khi deploy: build lại image Agent; không cần migration hay đổi Gateway |

## Rollback

| Cách | Hiệu lực |
|---|---|
| Biến môi trường `OCR_PDF_TEXT_LAYER_FIRST=false` | Trả lại đường OCR mọi trang cũ |
| Biến môi trường `OCR_CORRECTION_DISABLE_THINKING=false` | Sửa dấu dùng thinking mặc định |
| `git revert` các file ở §7 | Gỡ toàn bộ thay đổi code |
