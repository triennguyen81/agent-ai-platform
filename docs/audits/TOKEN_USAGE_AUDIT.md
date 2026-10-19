# TOKEN USAGE AUDIT — AIQ Accounting Agent

**Ngày:** 2026-10-11

**Phạm vi:** chỉ audit. Không sửa source code, không ghi dữ liệu production. Mọi truy vấn production đều là `BEGIN READ ONLY … ROLLBACK`; transcript chỉ được đọc để lấy số.

## Quy ước bằng chứng

| Nhãn | Nghĩa |
|---|---|
| **ĐO** | Số liệu thật lấy từ hệ thống đang chạy |
| **ƯỚC LƯỢNG** | Suy từ kích thước byte hoặc suy luận. **Không** phải số đo; luôn kèm cách kiểm chứng |
| **CHƯA ĐO** | Không có dữ liệu |

## 0. Nguồn dữ liệu thực tế

| Nguồn | Nội dung | Giới hạn |
|---|---|---|
| **Transcript của Claude Code CLI trong container Agent** (`/home/app/.claude/projects/-app/*.jsonl`): 219 session, 2026-09-09 → 2026-10-07 | `usage` thật **theo từng API call**: `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`, model, tool_use | Script chỉ in số và nhãn, không in nội dung. Không có giá tiền. Phân loại session theo marker cố định trong prompt (mục 0.1) |
| Postgres `agent_usage_events` (57 dòng, 2026-09-29 → 2026-10-07) | `cost_amount` thật (`ResultMessage.total_cost_usd`), output tokens, latency | `input_tokens` **sai** (mục 6); không có cache tokens; chỉ đường manifest |
| Prometheus `aiq_llm_*` | — | **Không có series nào**: endpoint `/metrics` của Agent đang tắt (`agent/src/aiq_agent/main.py:185`) |
| Redis (`MEMORY_BACKEND=redis`, lịch sử chat) | — | **Không đọc**: cần mật khẩu dịch vụ, không dùng secret cho audit |

### 0.1 Phân loại session từ transcript

| Mã | Nhận diện (marker trong user message đầu) | Đường code |
|---|---|---|
| A | `BLOCK 1` + `RAW OCR TEXT` | Trích xuất chứng từ: `/agent/document-extract` hoặc `/agent/run` legacy với prompt trích xuất |
| B | `USER REQUEST:` | Agent cấu hình (manifest), chat hoặc run: `ClaudeSdkAgentExecutor` |
| C | Không marker, **có** gọi tool | Legacy `run_task` (`/agent/run` không có agent) |
| D | Không marker, **không** gọi tool, 1 call | Chủ yếu là OCR correction (`DiacriticCorrector`, model `claude-sonnet-4-6` = mặc định `anthropic_model`); còn lại là chat thường / PEC |

## 1. Bảng xếp hạng thành phần tiêu tốn token

**Tổng ĐO trên 219 session / 416 LLM call:**
- prompt **11,817,901** token, trong đó cache read 9,429,680, cache write 2,387,342, không cache 879;
- output **424,845** token.

| # | Thành phần | Số liệu | Lưu lượng hiện tại | Mức |
|---|---|---|---|---|
| 1 | **Skill body nhúng toàn văn vào system prompt** của mọi call trích xuất và agent (9 skill, 55,461 byte) | ĐO: prompt call đầu, trung vị **32,031** (A) và **31,009** (B), trong khi nội dung thật của user/chứng từ trung vị chỉ 2,318 và 1,608 ký tự. ƯỚC LƯỢNG: skill ≈ 16–22k token mỗi call | Chính: A + B = mọi trích xuất và mọi chat agent | CAO NHẤT |
| 2 | **Định nghĩa tool built-in của Claude CLI** trong OCR correction và chat thường (`tools` không bị tắt) | ĐO: D trung vị **15,102** token prompt cho trung vị **470 ký tự** text; 1 call / 1 trang OCR. **Không** được ghi vào `agent_usage_events` | Có: 21 session ngày 2026-10-07 | CAO |
| 3 | **Legacy `run_task`**: `skills="all"`, built-in tools, MCP tool phải tìm bằng `ToolSearch` | ĐO: **6,144,123** prompt token = **52%** tổng; 3.86 call/session; 73 lần `ToolSearch` | Hiện **không** dùng (lần cuối 2026-09-18), nhưng đường code vẫn mở | CAO (lịch sử) / TRUNG BÌNH (hiện tại) |
| 4 | **Gửi lại toàn bộ prompt ở mỗi vòng agent** (multi-turn) | ĐO: B 1.87 call/session ⇒ ~87% prompt bị gửi thêm lần nữa; C 3.86 call/session | Có (B) | TRUNG BÌNH |
| 5 | **Cache write**: prefix lạnh ở mỗi session CLI mới | ĐO: 2,387,342 token cache write; tỷ lệ write / prompt: D 41%, A 31%, B 18% | Có | TRUNG BÌNH |
| — | Tool results | ĐO: 269 kết quả, tổng 329,773 ký tự, lớn nhất 10,336 ký tự | — | THẤP |
| — | RAG chunks | ĐO: Agent "Trợ lý kế toán" có **0** knowledge base / note; `rag_latency_ms = 0` ở cả 57 event | — | 0 hiện tại (rủi ro khi gắn KB) |
| — | Conversation history | ĐO: Postgres `chat_threads` = 0 thread; nội dung user message của B trung vị 1,608 ký tự (đã gồm history). Redis: CHƯA ĐO | — | THẤP hiện tại (rủi ro: mục 3) |
| — | Planner / Critic (PEC) | CHƯA ĐO riêng (nằm lẫn trong D). Chỉ chạy ở `/chat` có `tool` mà không có agent | — | THẤP |

**Chi tiết ĐO theo loại session:**

| Loại | Session | Call | Prompt call đầu (p10 / p50 / p90) | Tổng prompt | Tổng output | Model |
|---|---|---|---|---|---|---|
| A Trích xuất | 48 | 54 | 30,039 / 32,031 / 32,760 | 1,768,100 | 87,643 | sonnet-5 (29), sonnet-5-5 (18) |
| B Agent manifest | 55 | 103 | 6,888 / 31,009 / 32,329 | 2,495,238 | 91,567 | sonnet-5 (39), sonnet-5-5 (16) |
| C Legacy có tool | 50 | 193 | 23,551 / 23,564 / 47,711 | 6,144,123 | 134,218 | sonnet-5 (50) |
| D Single-shot không tool | 66 | 66 | 14,872 / 15,102 / 47,751 | 1,410,440 | 111,417 | sonnet-4-6 (49), sonnet-5 (17) |

**Chi phí ĐO** (chỉ A + B qua `agent_usage_events`; không có số cho C, D):

| event_type | n | output tok | Chi phí USD | TB / event |
|---|---|---|---|---|
| agent_run | 23 | 52,150 | 1.7415 | 0.0757 |
| document_extract | 31 | 17,405 | 1.5749 | 0.0508 |
| chat | 3 | 1,508 | 0.1590 | 0.0530 |
| **Tổng** (Trợ lý kế toán, model alias `sonnet`) | 57 | 71,063 | **3.4754** | 0.0610 |

Ví dụ: event `document_extract` #55 chỉ có **253** output token nhưng tốn **$0.1128**. Chi phí do phần input cố định quyết định, không phải do kết quả trích xuất.

## 2. Luồng Frontend → Gateway → Agent → LLM

```
Web (chat-room, upload)
 → Gateway
     ├─ chat / agent run  → Agent /chat, /agent/run            (manifest do Gateway ký)
     ├─ Hangfire extraction → Agent /agent/document-extract   (A, tool-less, maxTurns ≤ 2)
     │                       hoặc /agent/run legacy prompt    (A, maxTurns 3)
     └─ Hangfire OCR      → Agent /ocr/pdf, /ocr/image
                              → DiacriticCorrector: 1 Claude CLI session / trang     (D)
 Agent ClaudeSdkAgentExecutor (B, A):
   system prompt = platform policy + agent system prompt + company + TOÀN VĂN skill + KB notice + workflow
   query        = history (nếu có) + RAG evidence (nếu có KB) + "USER REQUEST"
   CLI          = tools=[] (tắt built-in), chỉ MCP tool được cấp, skills=filesystem_names
   → Claude CLI → Anthropic API: mỗi vòng tool gửi lại toàn bộ prefix + kết quả tool
```

**Số LLM call mỗi request (ĐO, trung bình):**

| Đường | Call / request |
|---|---|
| Trích xuất | 1.12 |
| Agent manifest | 1.87 (tối đa 4) |
| Legacy | 3.86 (tối đa 6) |
| OCR correction | 1 / **trang** |
| PEC | 2 (planner + critic; executor không gọi LLM) — theo code |

## 3. Bằng chứng theo file / hàm

| Hạng mục | File:dòng | Ghi chú |
|---|---|---|
| Skill nhúng toàn văn | `agent/src/aiq_agent/runtime/executors.py:195,198` (`_resolve_skills`), `:212-223` (`_system_parts`) | Mọi skill của manifest đều vào system prompt, kể cả skill không liên quan đến request |
| Skill **đồng thời** khai báo cho CLI | `runtime/executors.py:605` `skills=resolved_skills.filesystem_names` | SDK tự bật `setting_sources=["user","project"]` khi có `skills` (`claude_agent_sdk/_internal/transport/subprocess_cli.py`, `_apply_skills_defaults`). Thư mục `~/.claude/skills` trỏ tới 45 skill. Phần CLI nạp thêm từ đây: CHƯA ĐO |
| Trích xuất vẫn mang skill | `api/routes.py:760` | Bỏ tools / delegates / workflow nhưng **giữ skills** ⇒ 9 skill (contract, tax, url-ingest…) đi kèm một tác vụ chỉ cần trả JSON |
| Prompt trích xuất nhắc skill không được cấp | `gateway/.../Jobs/VoucherExtractJobRunner.cs` (`BuildExtractionPrompt`), `Vouchers/DocumentProcessing/DocumentExtraction.cs:46-64` | Yêu cầu áp dụng `voucher-universal-extractor`, `journal-entry-formatter`. Agent live **không** có hai skill này, nhưng lại có 9 skill khác |
| Schema của **mọi** loại được bật trong mỗi prompt | `DocumentExtraction.cs:46` `foreach (var type in enabled)` | Đúng thiết kế (để mô hình tự phân loại), nhưng tăng theo số loại |
| OCR correction không tắt built-in tools | `agent/src/aiq_agent/llm/claude_cli.py:74-80` (không có `tools=[]`); `ocr/pipeline.py` (`run_pages`: 1 call / trang); `main.py:53` | Model `anthropic_model` mặc định `claude-sonnet-4-6` (`config.py:35`) |
| Legacy `run_task` | `agent/src/aiq_agent/agentic/runner.py:97` `skills="all"`, `:166-172` (không có `tools=[]`) | Built-in tools + mọi skill + MCP tool bị trì hoãn ⇒ `ToolSearch` |
| Tool echo raw OCR | `agentic/voucher_tools.py:98,119` | `extract_voucher_fields`: mô hình phải viết lại raw text (≤ 50,000 ký tự) vào tham số (**output token**), tool trả nguyên văn lại. Đã gọi 10 lần (đường C) |
| History | `api/routes.py:199` (`/chat`: `limit = memory_limit`, mặc định 20); `:624` (`/agent/run`: **không giới hạn**); `runtime/executors.py:547` | Toàn bộ history nối thành text trong query. Câu trả lời lưu vào history là **mọi** text block của run (`executors.py:649`). Trần lưu trữ `memory_max_messages = 200` (`config.py:84`) |
| RAG | `knowledge/service.py` (`retrieve`), `config.py:59,65` | Tối đa `qdrant_top_k = 10` chunk và `knowledge_context_chars = 12,000` ký tự JSON mỗi request; tool `search_notes` có thể gọi thêm |
| Kích thước tool | ĐO trong image: 23 tool được cấp ≈ 7–8 KB mô tả + schema | Không đáng kể |
| Tool result | `agentic/report_tools.py:246,262` | Lấy tối đa 200 dòng nhưng chỉ in 30 ⇒ đã có giới hạn |

## 4. Nguyên nhân năm nguồn lãng phí lớn nhất

**1. Skill nạp tĩnh, toàn văn, theo agent chứ không theo request.**
- Agent kế toán gắn 9 skill: contract-extractor 13.4 KB, accounting-vietnam 10.8 KB, voucher-invoice-vat 8.3 KB, tax-calculation 6.2 KB…
- Toàn bộ đi vào **mọi** call, kể cả trích xuất JSON 1 lượt.
- Các skill đều ≤ 13 KB, nhưng cộng lại ~55 KB.
- *Suy luận (ƯỚC LƯỢNG):* các session B có p10 = 6,888 token (nhiều khả năng là agent ít hoặc không có skill), so với p50 = 31,009 ⇒ chênh lệch ~24k token / call do skill và các khối đi kèm.

**2. OCR correction đi qua CLI agent đầy đủ.**
- Mỗi trang là một session Claude Code mới.
- Không tắt built-in tools, nên khoảng ~14.6k token khung CLI (prompt p50 15,102 trừ khoảng ~470 ký tự text) được gửi để sửa dấu cho vài trăm ký tự.
- Không được tính vào usage nào ⇒ chi phí ẩn.

**3. Legacy agentic path.**
- `skills="all"` + built-in tools, cộng với cơ chế deferred tool, buộc mô hình gọi `ToolSearch` trước khi dùng MCP tool ⇒ thêm vòng LLM, mỗi vòng gửi lại khoảng ~24k token.
- ƯỚC LƯỢNG: 73 lần `ToolSearch` × ~23.5k ≈ 1.7M prompt token do riêng các vòng tìm tool.

**4. Agent loop gửi lại toàn bộ prefix ở mỗi vòng tool.**
- Đây là bản chất của tool-use, nhưng prefix 31k khiến mỗi vòng thêm đắt.
- Prefix nhỏ thì chi phí mỗi vòng giảm theo.

**5. Cache write lặp lại.**
- Mỗi request là một session CLI mới. Khi lưu lượng thưa (theo đợt upload), cache 5 phút hết hạn nên prefix 15–32k phải ghi lại.
- Giá ghi cache cao hơn giá đọc; tỷ lệ write cao nhất ở D (41%) và A (31%).

## 5. Phương án tối ưu, mức tiết kiệm dự kiến (ƯỚC LƯỢNG) và cách kiểm chứng

Kiểm chứng chung cho mọi mục: chạy lại script đọc transcript (chỉ số) trước và sau, so sánh **prompt call đầu p50**, **call / session** và **tỷ lệ cache write** theo loại A–D. Với A và B, so thêm `cost_amount` trong `agent_usage_events` trên cùng bộ tài liệu test.

| # | Phương án | Tiết kiệm dự kiến (ƯỚC LƯỢNG) | Cách kiểm chứng | Rủi ro |
|---|---|---|---|---|
| 1 | **Trích xuất (A):** không nhúng skill của agent vào `/agent/document-extract`. Prompt đã mang đủ schema từ cấu hình; chỉ nhúng skill gắn với **loại chứng từ** (đã có cơ chế `type.Skills`) | Prompt A từ ~32k xuống ~7–9k (**−70–78%**) | A/B trên cùng chứng từ tổng hợp: prompt call đầu, và độ khớp JSON trích xuất (schema_valid, missing_required trong `document_processing_runs`) | Chất lượng trích xuất nếu skill thực sự đang giúp; đo bằng kết quả schema |
| 2 | **OCR correction (D):** dùng `tools=[]` cho `ClaudeCliLlmService`; gộp nhiều trang vào 1 call; bỏ qua sửa lỗi khi độ tin cậy OCR cao (quyết định sản phẩm) | ~15k xuống ~1–2k token / call (**−85–93%**); số call giảm theo số trang gộp | Prompt call đầu của loại D; so sánh văn bản trước/sau sửa trên bộ mẫu | Gộp trang làm câu trả lời dài hơn; cần kiểm tra đầu ra giữ ngắt trang |
| 3 | **Agent chat (B):** chỉ nhúng skill theo request (chọn theo từ khoá / loại tác vụ như `select_skills` của `/chat`), hoặc để CLI nạp skill theo nhu cầu (chỉ mô tả, nội dung khi gọi `Skill`) thay vì inline. Bỏ trùng inline + `skills=` | ~31k xuống ~8–14k / call (**−55–75%**), nhân với 1.87 call / session | Prompt call đầu và call / session của B; kiểm thử hồi quy câu hỏi kế toán chuẩn | Mô hình có thể bỏ sót hướng dẫn nếu chọn skill sai |
| 4 | **Legacy `run_task` (C):** gỡ đường không-agent, hoặc đưa về cùng cấu hình executor (`tools=[]`, danh sách skill tường minh) | Lịch sử 6.1M prompt token; ~1.7M do `ToolSearch` (ƯỚC LƯỢNG) | Loại C không còn xuất hiện; `ToolSearch` = 0 | Admin mất đường chạy không cần agent (cần quyết định) |
| 5 | **History:** giới hạn `/agent/run` như `/chat`; chỉ lưu câu trả lời cuối thay vì mọi text block; tóm tắt khi dài | Hiện nhỏ (ĐO). Chặn tăng tuyến tính theo độ dài hội thoại | Độ dài phần history trong query (số ký tự) qua instrumentation mục 6 | Mất ngữ cảnh xa |
| 6 | **Cache:** giữ prefix tĩnh, giống hệt nhau và đặt trước (policy, system prompt, skill); phần động (company, workflow, RAG, history) đặt sau | Giảm tỷ lệ cache write (D 41%, A 31%) | Tỷ lệ `cache_creation / (cache_creation + cache_read)` theo loại | Không đáng kể |
| 7 | **Tool `extract_voucher_fields`:** truyền `voucher_id` thay cho raw text; tool tự lấy text từ Gateway | Bỏ 2 bản sao raw text (1 bản là output token) | Kích thước tool_use input / tool_result trong transcript | Không đáng kể |

## 6. Token accounting hiện tại và bổ sung đề xuất

### Hiện trạng (ĐO)

- **`input_tokens` ghi sai.**
  - `executors.py:653` chỉ lấy `usage["input_tokens"]`, tức phần **không cache**.
  - Kết quả: 57 event có tổng **124** input token, trong khi transcript cho thấy mỗi call ~15–32k token prompt.
  - `cache_creation_input_tokens` và `cache_read_input_tokens` bị bỏ.
- **`llmCallCount` cố định = 1** (`observability/usage_reporter.py:85`), trong khi thực tế là 1.12 / 1.87 / 3.86 call mỗi request.
- **Không ghi:**
  - OCR correction (D);
  - legacy `run_task` (C);
  - `/chat` không agent (bao gồm PEC);
  - delegate sub-execution (chỉ ghi kết quả của router).
- **Không ghi kích thước thành phần:** system prompt, skill, RAG, history, tool result.
  - `executors.py:577-587` có tính `diagnostics` (byte system prompt, số skill, số tool…) nhưng chỉ dùng khi lỗi E2BIG.
- **Prometheus tắt**, nên `RecordedLlmService` không có số để xuất.
  - Và vì `ClaudeCliLlmService` trả `usage = 0`, kể cả bật lên thì token của OCR correction vẫn bằng 0.
- **`cost_amount`** lấy từ `total_cost_usd` của CLI. Với xác thực subscription, đây là chi phí **quy đổi** do CLI báo, không nhất thiết bằng số tiền bị tính.

### Đề xuất (không triển khai trong audit này)

1. **Thêm cột** vào `agent_usage_events`:
   - `cache_creation_input_tokens`, `cache_read_input_tokens`;
   - `num_turns` (`ResultMessage.num_turns`);
   - `llm_call_count` thật (đếm `AssistantMessage` có id khác nhau).
2. **Lưu kích thước thành phần** vào `metadata` (byte + số lượng, không lưu nội dung):
   - `system_prompt_bytes`, `skills_bytes` (theo từng skill), `rag_chars` + `rag_chunks`, `history_chars` + `history_messages`, `user_prompt_chars`;
   - `tool_result_chars` (tổng / max) + `tool_calls`.
3. **Ghi usage cho OCR correction và các đường chưa ghi:** `event_type = ocr_correction`, `legacy_run`, `plain_chat`, `delegate`.
4. **Bật lại `/metrics`** sau khi sửa xung đột thư viện đã ghi ở `main.py:170-185`, và đưa cache tokens vào counter `aiq_llm_tokens_total{direction}`.
5. **Thêm báo cáo** theo Agent / Provider / Model / Request từ bảng trên (bảng đã có `agent_id`, `model`, `trace_id`).

## 7. Những gì chưa đo được và lý do

| Hạng mục | Lý do |
|---|---|
| Token chính xác của từng khối (skill, policy, company, khung CLI) | Transcript chỉ có tổng theo call; không có tokenizer / API key để đếm offline. Số ở mục 4–5 là **ƯỚC LƯỢNG** |
| Chi phí của C và D | Không có trong `agent_usage_events`; transcript không ghi giá. Không quy đổi token ra tiền vì chưa xác nhận bảng giá của các model `claude-sonnet-5` / `5-5` / `4-6` |
| Lịch sử chat thật trong Redis | Cần mật khẩu dịch vụ; không dùng secret cho audit |
| Phân tách D giữa OCR correction và chat thường / PEC | Prompt hệ thống không có trong transcript. Phân loại dựa trên model (49/66 là `sonnet-4-6` = mặc định của corrector) |
| Nội dung CLI tự nạp từ `setting_sources` (skill listing) | Không quan sát được system prompt cuối cùng mà CLI gửi đi |

## 8. Phạm vi thao tác trong audit

- **Đã chạy:**
  - SELECT read-only trên Postgres production;
  - đọc metadata container (`docker inspect`, `ls`);
  - chạy script đếm số trên transcript CLI trong container Agent (script tạm đã xoá);
  - đo kích thước tool / skill trong container cô lập `--network none`.
- **Không:**
  - sửa code hay cấu hình;
  - gọi LLM;
  - ghi dữ liệu;
  - in hoặc lưu nội dung prompt / tài liệu.
