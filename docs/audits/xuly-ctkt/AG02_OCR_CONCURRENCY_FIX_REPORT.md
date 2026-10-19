# AG-02 — OCR Concurrency Crash: Fix Report

**Trạng thái:** đã xác định nguyên nhân gốc, đã tái hiện crash native ở mức thành phần, đã sửa và xác minh bằng stress test thật (không mock) trên môi trường cô lập.

**Chưa deploy production. Chưa commit.**

## 1. Root cause và bằng chứng

### Kết luận

PDFium (qua `pypdfium2`) bị gọi **đồng thời từ nhiều thread** trong cùng process Agent:
- Mọi bước native của OCR chạy bằng `asyncio.to_thread`, tức pool thread mặc định, không giới hạn theo OCR.
- Hai request đồng thời vì vậy chạy PDFium song song.

PDFium "inherently not thread-safe": đây là giới hạn được nhà phát triển ghi rõ trong `pypdfium2` METADATA, mục *Known limitations → Incompatibility with Threading*. Hậu quả là hỏng heap, rồi process abort/segfault, và toàn bộ request đang chạy trong Agent chết theo.

### Phân loại (theo yêu cầu mục 1)

| Loại | Kết luận | Bằng chứng |
|---|---|---|
| Python exception | Không phải nguyên nhân crash. Có xuất hiện như **triệu chứng phụ**: `PdfiumError: Data format error` trên PDF hợp lệ, `AssertionError: Some kids weakrefs have not been cleaned up` | Stress thành phần (mục 6.1) |
| Native crash / segfault | **Đúng: nguyên nhân gốc** | Live: `docker logs aiq-agent` có dòng `realloc(): invalid old size` (glibc abort do heap hỏng), `RestartCount=1`. Cô lập: **4/15 lần chạy exit 139 (SIGSEGV)** trên code cũ |
| OOMKilled | Không phải nguyên nhân của sự cố đã ghi nhận | `State.OOMKilled=false`; container không có memory limit (`HostConfig.Memory=0`). **Rủi ro OOM riêng** với ảnh rất lớn: xem mục 8 |
| Worker timeout / restart | Không | uvicorn 1 process, không có timeout worker. Restart là do process chết |
| Gateway mất kết nối, Agent vẫn chạy | Không | Agent process chết thật (đã restart) |

### Vì sao `OcrGate` ở Gateway không đủ

- `Books/IAgentClient.cs:99` chỉ tuần tự hoá `/ocr/pdf` và `/ocr/image`, và chỉ trong **một** process Gateway.
- Các đường sau gọi OCR / PDFium của Agent **không qua** gate:

| Gateway | Agent route |
|---|---|
| `Endpoints/ContractEndpoints.cs:174`, `Endpoints/AccountingEndpoints.cs:116` | `/documents/extract` (đọc text layer PDFium + OCR) |
| `Endpoints/KnowledgeBaseEndpoints.cs:105` | `/knowledge/extract` (render PDFium) |

Upload chứng từ cùng lúc với upload hợp đồng / tài liệu tri thức vẫn chạy PDFium song song trong Agent.

### Mã liên quan trước khi sửa

- `agent/src/aiq_agent/ocr/pdf.py`: `render_pdf_pages_async` và `extract_pdf_text_layer_async` dùng `asyncio.to_thread`.
- `agent/src/aiq_agent/ocr/extractor.py`: `PaddleOcrExtractor.extract`, `RapidOcrExtractor.extract` dùng `asyncio.to_thread`.
- `agent/src/aiq_agent/ocr/image.py`: `normalize_image_to_png_async` dùng `asyncio.to_thread`.
- `agent/src/aiq_agent/api/knowledge_routes.py`: `asyncio.to_thread(render_pdf_pages, …)`.
- RapidOCR engine dùng chung một instance. Đo riêng (8 thread × 4 vòng, 2 lần) thì **không** lỗi; ONNX Runtime `run` là thread-safe.

## 2. File và dòng code thay đổi

| File | Thay đổi |
|---|---|
| `agent/src/aiq_agent/ocr/concurrency.py` (mới) | `PDFIUM_LOCK` (`:49`); `OcrWorkGate` (`:68`): thread pool riêng có giới hạn, backlog giới hạn, timeout; `get_ocr_gate` (`:131`); `run_ocr_native` (`:142`); `OcrBusyError` (429), `OcrTimeoutError` (504) |
| `agent/src/aiq_agent/ocr/pdf.py:30,68` | Toàn bộ thao tác PDFium nằm trong `PDFIUM_LOCK` |
| `agent/src/aiq_agent/ocr/pdf.py:46,85` | Bản async chạy qua gate |
| `agent/src/aiq_agent/ocr/extractor.py:76,145` | RapidOCR / Paddle chạy qua gate |
| `agent/src/aiq_agent/ocr/image.py:46` | Giải mã / chuẩn hoá ảnh chạy qua gate |
| `agent/src/aiq_agent/api/knowledge_routes.py:32` | Render PDF qua gate |
| `agent/src/aiq_agent/api/knowledge_routes.py:44-45` | Trả 429 / 504 |
| `agent/src/aiq_agent/api/ocr_routes.py:41-43,96,137,152` | 429 (kèm `Retry-After`) / 504 |
| `agent/src/aiq_agent/documents/extraction.py:191-193` | Giữ mã 429 / 504, không bị gộp thành 422 |
| `agent/src/aiq_agent/config.py:75-79` | `ocr_max_concurrent_jobs`, `ocr_queue_capacity`, `ocr_job_timeout_seconds` |
| `agent/src/aiq_agent/main.py:58,121` | Tạo gate khi khởi động (cấu hình sai thì fail ngay lúc boot); `shutdown()` khi tắt |
| `gateway/src/Aiq.Gateway/Vouchers/IVoucherRepository.cs:118,949` | `TryUpdateOcrJobResultAsync`: chỉ ghi khi `status IN ('processing','ocr_unavailable')` |
| `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs:37` | `ExceptOn = VoucherOcrSupersededException` |
| `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs:53,102,126,145,157` | Mọi lần ghi đều có điều kiện trạng thái |
| `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs:119` | Agent 429 / 504 ⇒ thử lại qua Hangfire |
| `gateway/src/Aiq.Gateway/Jobs/VoucherOcrJobRunner.cs:175` | `VoucherOcrSupersededException` |
| `gateway/src/Aiq.Gateway/Books/IAgentClient.cs:90-98` | Cập nhật chú thích `OcrGate` (giữ nguyên hành vi) |
| `agent/tests/test_ocr_concurrency.py` (mới) | 8 test |
| `gateway/tests/Aiq.Gateway.Tests/Data/VoucherOcrJobIdempotencyIntegrationTests.cs` (mới) | 10 test |

Không sửa module ngoài phạm vi.

## 3. Kiến trúc trước / sau

**Trước**

```
Gateway ─(OcrGate 1/process: chỉ /ocr/pdf, /ocr/image)─┐
Gateway ─(/documents/extract, /knowledge/extract: không gate)─┤
                                                         ▼
Agent (1 process uvicorn) ── asyncio.to_thread (pool mặc định, dùng chung)
     ├─ PDFium render / text layer   ← nhiều thread cùng lúc ⇒ hỏng heap ⇒ SIGSEGV / abort
     ├─ PIL decode
     └─ RapidOCR (instance chung)
```

**Sau**

```
Gateway ─(OcrGate giữ nguyên: điều tiết phía client)─┐
Gateway ─(/documents/extract, /knowledge/extract)───┤
                                                    ▼
Agent ── OcrWorkGate (mọi route OCR)
     │     backlog ≤ OCR_QUEUE_CAPACITY, vượt ⇒ 429 + Retry-After
     │     chờ + chạy ≤ OCR_JOB_TIMEOUT_SECONDS, vượt ⇒ 504
     │       (task native vẫn giữ slot đến khi thật sự xong; task bị bỏ khi còn trong hàng đợi thì không chạy)
     ├─ pool riêng "ocr-native", OCR_MAX_CONCURRENT_JOBS worker (mặc định 1)
     │     ├─ PDFium: luôn trong PDFIUM_LOCK (một thread tại một thời điểm, kể cả khi worker > 1)
     │     ├─ PIL decode
     │     └─ RapidOCR
     └─ LLM correction: ngoài gate, nên LLM chậm không giữ slot OCR
Gateway VoucherOcrJobRunner:
     ├─ 429 / 504 ⇒ ocr_unavailable "sẽ tự thử lại" + Hangfire retry (2 lần)
     └─ chỉ ghi khi voucher còn chờ OCR; chứng từ đã đi tiếp ⇒ không ghi, job Failed không retry,
        nên continuation trích xuất không chạy
```

## 4. Danh sách thay đổi hành vi

1. PDFium được tuần tự hoá trong toàn process Agent, áp dụng cho **mọi** route.
2. Công việc OCR native chạy trên pool riêng có giới hạn. Pool mặc định của asyncio không còn bị OCR chiếm.
3. Backpressure: backlog đầy ⇒ HTTP 429 `Retry-After: 10` thay vì dồn bộ nhớ.
4. Timeout theo từng task native ⇒ HTTP 504. Giới hạn đồng thời vẫn đúng khi timeout.
5. Graceful shutdown: task đang chờ bị huỷ; task đang chạy chạy nốt.
6. Gateway, idempotency khi retry:
   - Job OCR (kể cả lần retry sau khi Agent chết) **không ghi đè** chứng từ đã có text người dùng dán, đã trích xuất, đã duyệt hoặc ghi sổ.
   - Kết quả OCR về muộn bị loại bỏ.
7. Gateway: 429 / 504 từ Agent được coi là lỗi tạm thời (retry), không thành `ocr_unavailable` vĩnh viễn.
   - 503 giữ nguyên hành vi cũ: chuyển sang nhập tay.

## 5. Cấu hình worker / queue

Kiểm tra thực tế: host có 8 vCPU và 15 GiB RAM (khoảng 6 GiB còn trống); container Agent không có giới hạn CPU / RAM.
- `LLM_PROVIDER=claude_cli`.
- Ollama có chạy trên host nhưng không nạp model (`/api/ps` rỗng) và Agent không dùng ⇒ hiện không có cạnh tranh RAM với Qwen3.

| Biến môi trường | Mặc định | Ý nghĩa | Ghi chú |
|---|---|---|---|
| `OCR_MAX_CONCURRENT_JOBS` | 1 (1–8) | Số task OCR native song song | Đo được: 2 worker chỉ nhanh hơn ~5% nhưng RAM ×1.9 và CPU bão hoà (mục 6) |
| `OCR_QUEUE_CAPACITY` | 32 (0–1000) | Số task được chờ; vượt ⇒ 429 | |
| `OCR_JOB_TIMEOUT_SECONDS` | 300 | Thời gian tối đa chờ + chạy một task native ⇒ 504 | PDF 20 trang mất khoảng 100–150 s |
| `OCR_MAX_RETRIES` | — | **Không tạo mới** | Dùng cấu hình có sẵn: Hangfire `AutomaticRetry(Attempts = 2)` của job OCR voucher, có back-off sẵn của Hangfire |
| `OCR_WORKER_MEMORY_LIMIT` | — | **Không triển khai trong code** | Thread không giới hạn RAM riêng được. Đề xuất đặt `mem_limit` cho container Agent ở tầng deploy (mục 8) |

## 6. Benchmark trước / sau

### 6.1 Tái hiện crash ở mức thành phần

16 thread xen kẽ đọc text layer / render PDF, 60 vòng, PDF tổng hợp.

| | Số lần chạy | Crash SIGSEGV (exit 139) | Lần có lỗi giả trên PDF hợp lệ | Thời gian 10 lần |
|---|---|---|---|---|
| Trước | 15 | **4** | 2 (trong đó có lần 213 lỗi) | 157 s (có crash sớm) |
| Sau | 15 | **0** | **0** | 498 s |

Đọc text layer (8 thread × 40 vòng), code cũ: 14–15 `PdfiumError` mỗi lần. Đây là chi phí thông lượng của việc tuần tự hoá PDFium trong stress tổng hợp. Ở tải HTTP thực tế, chi phí này không đáng kể (bảng dưới).

### 6.2 Tải HTTP

Route thật (`/ocr/pdf`, `/ocr/image`, `/documents/extract`), engine RapidOCR thật; bước LLM correction thay bằng độ trễ cố định 0.3 s / trang. Môi trường: container cô lập, mạng internal, `--restart unless-stopped`.

| Kịch bản | Trước: wall / TB job / CPU peak / RAM peak | Sau: wall / TB job / CPU peak / RAM peak | Kết quả |
|---|---|---|---|
| 1 PDF (2 trang) | 20.9 s / 20.9 s / 411% / 560 MiB | 20.5 s / 20.5 s / 400% / 712 MiB | 200 |
| 2 PDF | 36.0 s / 36.0 s / 503% / 1,610 MiB | 35.9 s / 27.2 s / 404% / 819 MiB | 200 ×2 |
| 5 PDF | 88.1 s / 86.9 s / 709% / 3,449 MiB | 88.2 s / 55.5 s / 408% / 819 MiB | 200 ×5 |
| 10 PDF | 164.1 s / 159.5 s / 737% / **4,903 MiB** | 176.4 s / **99.9 s** / 407% / **967 MiB** | 200 ×10 |
| 10 ảnh | 65.0 s / 62.9 s / 735% / 2,589 MiB | 67.6 s / 39.1 s / 407% / 658 MiB | 200 ×10 |
| 10 hỗn hợp (pdf / image / documents) | 132.4 s / 108.0 s / 732% / 3,999 MiB | 141.2 s / 75.1 s / 409% / 1,041 MiB | 200 ×10 |
| PDF 20 trang + 4 PDF | 238.0 s / 113.7 s / 735% / 3,247 MiB | 248.9 s / 91.0 s / 409% / 1,043 MiB | 200 ×5 |
| File hỏng (pdf / jpg / extract) + 3 PDF | 50.3 s / 642% / 1,807 MiB | 55.6 s / 407% / 948 MiB | 503 / 400 / 422 cho file hỏng; 200 ×3. Không ảnh hưởng job khác |
| Ảnh 6000×8000 + 2 ảnh | 53.1 s / 677% / **6,896 MiB** | 54.2 s / 407% / **6,762 MiB** | 200 ×3. Xem rủi ro mục 8 |

CPU / RAM peak được tính từ toàn bộ mẫu `docker stats` thô của từng lần chạy (12–126 mẫu mỗi kịch bản).

Ở mọi kịch bản HTTP, trước và sau: Agent restart **0**, OOMKilled **false**, không có job mất, không có job xử lý trùng.

**Tải HTTP không tái hiện được abort** trong lần đo này. Crash chỉ tái hiện ở mức thành phần, khi nhiều lời gọi PDFium chồng nhau dày đặc hơn.

**Cấu hình khác (sau sửa):**

| Cấu hình | Kịch bản | Kết quả |
|---|---|---|
| `OCR_QUEUE_CAPACITY=2` | 10 ảnh | 200 ×3, **429 ×7**, RAM 357 MiB. Backpressure hoạt động |
| `OCR_MAX_CONCURRENT_JOBS=2` | 10 PDF | 166.9 s, TB 102.2 s, CPU 681%, RAM 1,799 MiB |
| `OCR_MAX_CONCURRENT_JOBS=2` | 10 ảnh | 64.4 s, CPU 714%, RAM 1,127 MiB |
| `OCR_JOB_TIMEOUT_SECONDS=5` | PDF 20 trang | **504 ×2**, Agent không restart |

**Tóm tắt:**
- Thông lượng gần như không đổi (+0–7% wall).
- Độ trễ trung bình **giảm 30–40%** nhờ xử lý FIFO thay vì chia CPU cho tất cả.
- RAM peak dưới tải (≥ 2 job) **giảm 2–5×** (ví dụ 10 PDF: 4,903 ⇒ 967 MiB); với 1 job thì tương đương.
- CPU không còn bão hoà 8 core: ~4 core cho ONNX, phần còn lại dành cho Gateway / Postgres / LLM.

## 7. Test đã chạy

| Bộ test | Môi trường | Kết quả |
|---|---|---|
| Agent `pytest tests` (toàn bộ) | Image Agent, `--network none` | **352 passed, 8 skipped** (trước: 344 / 8; thêm 8 test mới) |
| `test_ocr_concurrency.py` | Như trên | 8/8. Gồm test **PDFium thật, nhiều thread** (`test_concurrent_pdfium_use_from_threads_is_correct`), cùng loại tải từng gây lỗi ở code cũ |
| Gateway `VoucherOcrJobIdempotencyIntegrationTests` (mới) | Postgres cô lập (pgvector:pg16, tmpfs) | 10/10 |
| Gateway hồi quy: `AgentClient` 3, `TaxVerification` 39, `DocumentGate*` 46, `DocumentProcessing*` 41, `DocumentExtractionTenantIsolation` 7, `ExpenseClaim*` (gồm multi-file) 77, `VoucherTypeClassifier` 34, `AdminDepartmentAuthorityInvariant` 13, `TenantStorage` 4, `ContractProcessing` 11 | Như trên | **285/285 PASS** (gồm 10 test mới). DB cô lập ghi nhận 1,585 commit / 29,404 dòng insert, nên test chạy thật; sau đó còn 0 database tạm |
| Stress thành phần trước / sau | Container cô lập | Mục 6.1 |
| Tải HTTP trước / sau | Container cô lập | Mục 6.2 |

**Tenant isolation:** gate chỉ giữ bytes của request trong bộ nhớ của lời gọi đó, không có cache hay trạng thái chia sẻ giữa các request. Ranh giới tenant vẫn do Gateway quyết định như trước. Test `DocumentExtractionTenantIsolation` và `DocumentGateHardening` (cross-tenant actor) đều PASS.

**Không** thực hiện:
- ghi DB live, sửa chứng từ thật, đổi mật khẩu;
- deploy hay rebuild image production;
- commit.

Mọi test dùng dữ liệu tổng hợp (chuỗi "HOA DON TEST …").

## 8. Rủi ro còn lại

| Rủi ro | Mức | Ghi chú / đề xuất |
|---|---|---|
| **Ảnh rất lớn gây OOM:** một ảnh 6000×8000 dùng ~6.9 GiB RAM trong RapidOCR (đo riêng: chuẩn hoá ảnh 558 MiB, inference 6.9 GiB). Gate giới hạn số job, nhưng **không** giới hạn bộ nhớ của một job. Trên host còn ~6 GiB trống, một ảnh như vậy có thể khiến kernel kill process Agent | HIGH | Đã thử giảm kích thước: 4096 px ⇒ còn 420 / 2013 ký tự; 3000 px ⇒ 0 ký tự. **Không** áp dụng vì làm hỏng kết quả OCR. Cần quyết định sản phẩm: giới hạn số megapixel khi upload (từ chối kèm thông báo rõ), hoặc cắt ảnh thành ô (tile). Đề xuất thêm `mem_limit` cho container Agent để OOM chỉ ảnh hưởng Agent, không ảnh hưởng cả host |
| Crash native trong một file đơn lẻ (không do đồng thời) vẫn làm chết process Agent | MEDIUM | Không quan sát thấy với file hỏng đã thử. Cô lập bằng worker process riêng là bước tiếp theo nếu xuất hiện; **chưa** triển khai vì chưa có bằng chứng |
| Bước LLM correction (`claude_cli`: mỗi trang sinh một subprocess Claude CLI) không có giới hạn đồng thời | MEDIUM | Nằm ngoài gate theo thiết kế. Với `OcrGate` ở Gateway, voucher vẫn chạy tuần tự; nhưng `/documents/extract` có thể chạy song song |
| `OcrGate` ở Gateway là giới hạn theo từng process | LOW | Agent đã tự bảo vệ, nên đây chỉ là điều tiết |
| Job OCR bị "superseded" kết thúc ở trạng thái Failed (không retry); continuation trích xuất ở lại trạng thái Awaiting trên dashboard Hangfire | LOW | Có chủ đích: chặn trích xuất lại chứng từ đã đi tiếp. Việc chặn ghi đè chứng từ đã ghi sổ **nói chung** vẫn là R-01 (`06_GAP_ANALYSIS.md`) |
| Crash production chưa được tái hiện qua HTTP | — | Chỉ tái hiện ở mức thành phần (4/15). Khớp với log live và tài liệu của nhà phát triển, nhưng **chưa xác minh trên production** vì chưa deploy |
| Backend `paddle` (`AIQ_OCR_BACKEND=paddle`) | LOW | Cũng đi qua gate, nhưng không được đo |

## 9. Hướng dẫn rollback

Thay đổi nằm hoàn toàn trong code. Không có migration DB, không đổi schema hay dữ liệu.

**Rollback Agent:**

1. Khôi phục các file Agent:
   ```
   git checkout -- agent/src/aiq_agent/ocr/pdf.py agent/src/aiq_agent/ocr/extractor.py \
     agent/src/aiq_agent/ocr/image.py agent/src/aiq_agent/api/ocr_routes.py \
     agent/src/aiq_agent/api/knowledge_routes.py agent/src/aiq_agent/documents/extraction.py
   ```
2. Xoá `agent/src/aiq_agent/ocr/concurrency.py` và `agent/tests/test_ocr_concurrency.py`.
3. Bỏ khối `ocr_*` trong `config.py` và 3 dòng `ocr_gate` trong `main.py`.
   - **Không** dùng `git checkout` cho hai file này vì chúng đang chứa thay đổi chưa commit khác.
4. Rebuild image Agent.

**Rollback Gateway:**

1. Khôi phục `VoucherOcrJobRunner.cs` về các lời gọi `UpdateOcrResultAsync` cũ.
2. Bỏ `TryUpdateOcrJobResultAsync` trong `IVoucherRepository.cs`, rồi xoá test `VoucherOcrJobIdempotencyIntegrationTests.cs`.
   - Hai file `.cs` này cũng có thay đổi chưa commit khác ⇒ sửa tay theo diff, không checkout cả file.

**Giảm thiểu nhanh, không cần rollback:**
- Đặt `OCR_QUEUE_CAPACITY` lớn hơn nếu gặp 429.
- Tăng `OCR_JOB_TIMEOUT_SECONDS` nếu gặp 504.
- **Không** tăng `OCR_MAX_CONCURRENT_JOBS` để tránh 429: PDFium vẫn an toàn vì có lock riêng, nhưng RAM tăng gần gấp đôi.
