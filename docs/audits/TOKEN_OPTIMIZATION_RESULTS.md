# TOKEN OPTIMIZATION RESULTS — AIQ Agent Runtime

**Ngày:** 2026-10-12

**Kế hoạch:** `TOKEN_OPTIMIZATION_PLAN.md`

**Trạng thái:** Phase 1–7 đã triển khai trong code. Chưa deploy, chưa commit, chưa gọi LLM trả phí.

## Quy ước số liệu

| Nhãn | Nghĩa |
|---|---|
| **ĐO** | Số liệu thật: production (audit 2026-10-11, read-only) hoặc chạy offline trên code thật |
| **ƯỚC LƯỢNG** | Tính toán, không phải số đo; luôn ghi công thức |
| **CHƯA ĐO** | Cần benchmark live có trả phí (`agent/scripts/token_benchmark_live.py`) và chưa được cho phép chạy |

## 1. Thay đổi theo phase

| Phase | Thay đổi | Mặc định | File chính |
|---|---|---|---|
| 1 Observability | Ghi `cache_creation_input_tokens`, `cache_read_input_tokens`, `llm_call_count` thật (đếm theo API `message_id`), `num_turns`, `request_id`, `cost_source`, metrics kích thước (chỉ số) | Bật | `observability/llm_usage.py` (mới), `observability/usage_reporter.py`, `runtime/executors.py`, `llm/claude_cli.py`, `llm/base.py`, `ocr/{corrector,pipeline}.py`, `api/{routes,ocr_routes,document_routes}.py`, `orchestration/pec.py`, `agentic/runner.py`; Gateway `Data/Migrations/066_agent_usage_token_detail.sql`, `Governance/IAgentUsageRepository.cs`, `Endpoints/AgentUsageEndpoints.cs`, `Books/IAgentClient.cs`, `Jobs/{VoucherOcrJobRunner,ExpenseClaimItemJobRunner}.cs`, `Endpoints/{Contract,Accounting}Endpoints.cs` |
| 2 Extraction | `extraction_manifest()`: không tool / delegate / workflow, `maxTurns ≤ 2`; chỉ giữ image skill **được prompt gọi tên**; luôn giữ skill declarative của tenant (trừ khi Gateway đã chèn); giữ system prompt của agent + khối hướng dẫn trích xuất; tắt skill discovery của CLI | Bật (`EXTRACTION_LEAN_PROMPT=true`) | `runtime/executors.py`, `api/routes.py` |
| 3 OCR | `ClaudeCliLlmService` dùng `tools=[]` (tắt built-in tools của CLI); batch nhiều trang / call có marker `<<<PAGE n>>>`, kiểm tra chặt, sai thì fallback từng trang | `tools=[]` bật; batch **tắt** (`OCR_CORRECTION_BATCH_CHARS=0`) | `llm/claude_cli.py`, `ocr/{corrector,pipeline}.py`, `main.py` |
| 4 Dynamic skill | Chat / run: inline skill declarative và `always_on`; catalog cố định cho skill còn lại; chọn trước theo keyword, đưa vào **user message**; tool `load_skill` chỉ trả skill đã gán cho phiên bản | **Tắt** (`DYNAMIC_SKILL_LOADING=false`) | `runtime/executors.py` (`_plan_skills`, `_build_load_skill_tool`) |
| 5 Legacy | `/agent/run` không agent ⇒ 403 nếu `LEGACY_AGENT_RUN_ENABLED=false`; code `run_task` giữ nguyên. Sửa BFF Web: chuyển agent đã chọn / dự phòng (trước đây bị bỏ) | Chặn | `api/routes.py`, `web/src/app/api/agent/run/route.ts` |
| 6 Context | History gửi cho mô hình: tối đa `HISTORY_MAX_MESSAGES=20` và `HISTORY_MAX_CHARS=24000`, nguyên message, áp cho cả `/agent/run` (trước: không giới hạn). Thread chỉ lưu câu trả lời **sau tool call cuối**. Trần `AGENT_MAX_TURNS_CAP=12` | Bật | `memory/base.py` (`trim_history`), `api/routes.py`, `runtime/executors.py` |
| 7 Benchmark | Offline `agent/scripts/token_benchmark.py`; live (trả phí, có khoá) `agent/scripts/token_benchmark_live.py` | — | — |

### Phát hiện trong quá trình triển khai (đã xử lý)

1. **Phase 2:** test `test_published_skill_reaches_document_runtime` khẳng định skill declarative đã publish **phải** tới runtime trích xuất. Quy tắc ban đầu ("chỉ skill được gọi tên") sẽ làm gãy hợp đồng này ⇒ sửa: skill declarative luôn được giữ.
2. **Phase 2:** system prompt của agent live có quy tắc nghiệp vụ dùng cho trích xuất (xác định công ty mình là bên nào) ⇒ **giữ**, không thay bằng prompt mới.
3. **Phase 2 (giữ nguyên, báo cáo):** prompt trích xuất của Gateway yêu cầu `voucher-universal-extractor` và `journal-entry-formatter`, nhưng agent live **không** được gán hai skill này. Runtime không tự thêm skill ngoài phiên bản (sẽ vượt cấu hình đã publish). Cần quyết định cấu hình.
4. **Phase 4:** catalog ban đầu thay đổi theo request ⇒ phá prefix cache. Đã sửa: catalog liệt kê mọi skill động, cố định theo phiên bản (test kiểm chứng system prompt giống hệt nhau giữa các request).
5. **Phase 6:** 3 phiên bản đã publish có `max_turns = 12` (ĐO, read-only) ⇒ trần mặc định đặt 12 để không agent hiện có nào bị cắt.
6. **Phase 5:** Gateway luôn gửi `capability`, nên đường legacy chỉ có thể tới từ BFF Web. Lỗi của BFF là tính agent dự phòng rồi bỏ đi.
7. **Benchmark:** chấm điểm từng đổi "0001234" thành 1234 ⇒ so sánh sai số hoá đơn. Đã sửa; có test.

## 2. Before / After

### 2.1 Token (call đầu của request)

ĐO cấu trúc bằng code thật + skill thật. Token sau tối ưu là ƯỚC LƯỢNG.

| Kịch bản | Skill gửi | Ký tự prompt + tool (ĐO) | Giảm (ĐO) | Token call đầu: trước (ĐO production, p50) → sau (ƯỚC LƯỢNG) |
|---|---|---|---|---|
| Trích xuất, prompt cấu hình | 9 → 1 | 45,567 → 11,802 | **74%** | 32,031 → 8,296–13,400 |
| Trích xuất, prompt legacy | 9 → 2 | 45,129 → 18,235 | **60%** | 32,031 → 12,943–17,047 |
| Chat: công nợ | 9 → 3 | 50,224 → 22,598 | 55% | 31,009 → 13,952–17,741 |
| Chat: thuế GTGT | 9 → 3 | 50,238 → 18,998 | 62% | 31,009 → 11,726–16,010 |
| Chat: checklist hợp đồng | 9 → 3 | 50,232 → 26,334 | 48% | 31,009 → 16,256–19,533 |
| Chat: định khoản | 9 → 4 | 50,231 → 25,137 | 50% | 31,009 → 15,518–18,959 |
| Chat: chào hỏi | 9 → 1 | 50,193 → 11,404 | 77% | 31,009 → 7,045–12,368 |

Ghi chú bảng:
- **Ký tự prompt + tool** gồm system prompt + user message + mô tả và schema tool.
- **Phạm vi phase:** dòng trích xuất là Phase 2 (bật); dòng chat là Phase 4 (**tắt mặc định**).
- **Công thức ƯỚC LƯỢNG** (`token_benchmark.estimate`), với *r* = ký tự sau / ký tự trước:
  - cận dưới = token đo × *r*;
  - cận trên = 6,888 + (token đo − 6,888) × *r*. Số 6,888 là prompt agent nhỏ nhất đo được (B p10), coi như overhead CLI / tool không giảm.

| Kịch bản | Trước (ĐO) | Sau |
|---|---|---|
| OCR correction: token / call | p50 **15,102** prompt token cho ~470 ký tự | `tools=[]` bỏ định nghĩa built-in tool của CLI. Token: **CHƯA ĐO**. Trong ~15.1k của baseline, phần văn bản và system prompt chỉ cỡ vài trăm ký tự (ƯỚC LƯỢNG: < 2k token sau tối ưu) |
| OCR correction: số call, tài liệu 5 trang | 5 call (ĐO, offline) | 1 call khi bật batch 8,000 ký tự (ĐO, offline) |
| History gửi lại (`/agent/run`) | Không giới hạn | ≤ 20 message và ≤ 24,000 ký tự |
| Câu trả lời lưu vào thread | Mọi text block của run | Chỉ đoạn sau tool call cuối |

### 2.2 Chi phí, latency, độ chính xác

| Chỉ số | Trước (ĐO, production) | Sau |
|---|---|---|
| Chi phí TB / `document_extract` | $0.0508 (31 event) | **CHƯA ĐO** |
| Chi phí TB / `agent_run` | $0.0757 (23 event) | **CHƯA ĐO** |
| Chi phí TB / `chat` | $0.0530 (3 event) | **CHƯA ĐO** |
| Latency TB `document_extract` | 6,456 ms | **CHƯA ĐO** |
| Độ chính xác trích xuất (trường vàng: loại, số, ngày, tổng tiền) | CHƯA ĐO (chưa có golden set trước đây) | **CHƯA ĐO.** Script live chấm 4 chứng từ tổng hợp, baseline và optimised trên cùng đầu vào |
| Độ khớp OCR correction | CHƯA ĐO | **CHƯA ĐO.** Script live so với văn bản chuẩn, theo từng trang và theo batch |

**Lý do CHƯA ĐO:** cần gọi Claude thật. Yêu cầu nêu "không gọi API LLM tính phí khi chưa được cho phép".

Script đã sẵn sàng: `PYTHONPATH=src python scripts/token_benchmark_live.py --confirm-paid-llm --max-usd 3 --out live.json`, chạy trong container Agent.
- 8 lượt trích xuất + 10 lượt chat + 2 lượt OCR correction.
- Có trần chi phí, tự dừng khi vượt.
- Không gọi Gateway hay DB.

## 3. Kiểm thử đã chạy

| Bộ | Kết quả |
|---|---|
| Agent `pytest` toàn bộ (image Agent, `--network none`) | **394 passed, 8 skipped**. Trước đợt này: 352 / 8. Thêm 42 test: `test_token_usage.py` 8, `test_token_optimization.py` 30, `test_token_benchmarks.py` 4 |
| Web | `tsc --noEmit` 0 lỗi; vitest **99/99** (thêm `api/agent/run/route.test.ts` 4) |
| Gateway: test Phase 1 trên Postgres cô lập | `AgentUsageTokenDetailIntegrationTests` 5/5; hồi quy `VoucherOcrJobIdempotency` 10/10, `ExpenseClaimMultiFile` 17/17, `ContractProcessing` 11/11 |
| Gateway toàn bộ theo từng class trên Postgres cô lập (khởi tạo từ init + 66 migration ⇒ 101 bảng; migration 066 áp dụng sạch) | Xem §3.1 |

Một lần chạy toàn bộ trong **một** process báo `53300: too many clients already`: các fixture cùng mở pool kết nối tới Postgres cô lập. Đây là giới hạn môi trường test, không phải lỗi sản phẩm; vì vậy test được chạy theo class như trong audit trước.

### 3.1 Gateway theo class

- **95 nhóm, 839 lượt chạy: 839 PASS, 0 FAIL.**
  - So với audit trước (824 lượt), thêm 10 test AG-02 và 5 test usage mới.
  - Bao gồm nhóm Thu tiền / Sao kê, Công nợ, Chiết khấu, Hợp đồng, Đề nghị thanh toán (một và nhiều file), Duyệt chứng từ, Xác minh thuế, ACL / tenant, ADMIN Department Authority Invariant và Migration.
- **Bằng chứng test chạy thật trên DB cô lập:** `xact_commit = 169,157`, `tup_inserted = 1,392,892`; còn 0 database tạm.
- Container và network cô lập đã xoá sau khi chạy.

## 4. Rủi ro còn lại

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| Phase 2 bỏ image skill không được gọi tên: chất lượng trích xuất có thể đổi nếu một skill bị bỏ đang âm thầm giúp | TRUNG BÌNH | Cờ `EXTRACTION_LEAN_PROMPT=false` khôi phục ngay. Benchmark live đo độ chính xác trước khi giữ cờ trên production |
| Phase 4 (khi bật): mô hình có thể không gọi `load_skill` khi cần | TRUNG BÌNH | Đang tắt; catalog + keyword là fallback; đo bằng benchmark live |
| Batch OCR (khi bật): mô hình trộn nội dung giữa các trang | THẤP | Kiểm tra marker chặt; sai thì fallback từng trang; đang tắt |
| Trần history 24k ký tự: hội thoại rất dài mất ngữ cảnh cũ | THẤP | Chỉnh qua `HISTORY_MAX_*`; thread lưu trữ không bị cắt |
| Chi phí CLI (`total_cost_usd`) là ước tính của CLI; với xác thực subscription không phải số tiền hoá đơn | — | Ghi rõ ở `cost_source` |
| Agent cũ (chưa deploy) gửi payload không có trường mới | THẤP | Mọi trường mới nullable / tuỳ chọn ở Gateway; OCR response mới có `llm_usage` tuỳ chọn |
| `/metrics` của Agent vẫn tắt (xung đột thư viện có từ trước) | — | Counter đã thêm nhãn cache; bật lại là việc riêng |
| Đường trích xuất legacy (agent không có cấu hình document processing) vẫn chạy qua `/agent/run` có tool | THẤP | Phase 2 chỉ áp cho `/agent/document-extract`; agent live đã có cấu hình |

## 5. Rollback

| Thành phần | Cách rollback | Cần deploy lại code? |
|---|---|---|
| Phase 2 | `EXTRACTION_LEAN_PROMPT=false` | Không (restart Agent) |
| Phase 3 batch | `OCR_CORRECTION_BATCH_CHARS=0` (mặc định) | Không |
| Phase 4 | `DYNAMIC_SKILL_LOADING=false` (mặc định) | Không |
| Phase 5 | `LEGACY_AGENT_RUN_ENABLED=true` (BFF vẫn gửi agent đã chọn) | Không |
| Phase 6 | `HISTORY_MAX_MESSAGES` / `HISTORY_MAX_CHARS` / `AGENT_MAX_TURNS_CAP` | Không |
| Phase 3 `tools=[]`, Phase 1 | `git revert` các file trong §1 | Có |
| Migration 066 | Không cần gỡ: chỉ thêm cột nullable, constraint, index, comment. Nếu bắt buộc: `ALTER TABLE agent_usage_events DROP COLUMN …` cho 5 cột, `DROP CONSTRAINT agent_usage_cache_tokens_check`, `DROP INDEX idx_agent_usage_events_request_id, idx_agent_usage_event_type_time` | — |

## 6. Việc cần quyết định

1. **Cho phép chạy benchmark live (trả phí, trần $3)** để có số Before/After thật về token, chi phí, latency và độ chính xác. Đây là điều kiện để giữ Phase 2 trên production, và để bật Phase 4 và batch OCR.
2. **Cấu hình agent:** gán `voucher-universal-extractor` / `journal-entry-formatter` cho agent kế toán (prompt trích xuất đang yêu cầu), hoặc bỏ tham chiếu khỏi prompt Gateway.
3. **Deploy:** chưa thực hiện.
