# TOKEN OPTIMIZATION PLAN — AIQ Agent Runtime

**Đầu vào:** `docs/audits/TOKEN_USAGE_AUDIT.md` (2026-10-11). Kế hoạch đã được đối chiếu lại với code ngày 2026-10-12. **Kết quả:** `TOKEN_OPTIMIZATION_RESULTS.md`.

## Nguyên tắc

- Mọi thay đổi có rủi ro chất lượng đều nằm sau **cờ cấu hình** để rollback không cần deploy lại code.
  - Thay đổi chỉ bỏ phần thừa (không đổi nội dung mô hình cần) thì bật mặc định.
  - Thay đổi có thể ảnh hưởng chất lượng trả lời thì **tắt mặc định** cho tới khi benchmark có trả phí được cho phép.
- Không đổi:
  - RBAC, tenant isolation;
  - danh sách tool được cấp, HITL;
  - workflow, quy tắc kế toán ở Gateway.
- Skill chỉ là văn bản hướng dẫn: việc chọn hay nạp skill không cấp thêm quyền.
- Usage log chỉ chứa số, mã cấu hình (model, provider, event) và id. **Không** chứa prompt hay nội dung chứng từ.
- Không gọi LLM trả phí, không ghi DB production, không đổi credential.

## Đối chiếu code (xác nhận các phát hiện của audit)

| Phát hiện | Vị trí xác nhận | Kết luận |
|---|---|---|
| Ghi `input_tokens` thiếu phần cache | `runtime/executors.py:653` | Đúng |
| `llmCallCount` cố định = 1 | `observability/usage_reporter.py:85` | Đúng |
| Skill toàn văn trong mọi call | `runtime/executors.py:195,198` | Đúng |
| Trích xuất giữ skill | `api/routes.py:760` | Đúng |
| OCR correction bật built-in tools | `llm/claude_cli.py:74-80` | Đúng (SDK chỉ thêm `--tools ""` khi `tools=[]`) |
| `skills=` làm SDK tự bật `setting_sources=["user","project"]` | SDK 0.2.163, `_apply_skills_defaults` | Đúng |
| Caller của legacy `run_task` | Gateway luôn gửi `capability` (`IAgentClient.cs:253`). **Chỉ** Web BFF `web/src/app/api/agent/run/route.ts` có thể gửi thiếu cả `agent_id` lẫn `capability`: route tính `agentId` dự phòng nhưng lại chuyển `body.agent_id` | Lỗi BFF khiến admin rơi vào legacy |
| `/agent/run` không giới hạn history | `api/routes.py:624` | Đúng |
| History lưu mọi text block | `runtime/executors.py:649` | Đúng |
| Usage của sub-agent được delegate không được ghi | `runtime/executors.py` (`_build_delegate_tool`) | Đúng |

## Các phase

| Phase | Thay đổi | Mặc định | Lý do mặc định |
|---|---|---|---|
| 1 Observability | **DB:** migration 066 thêm `cache_creation_input_tokens`, `cache_read_input_tokens`, `request_id`, `num_turns`, `cost_source`, metrics số trong `metadata`. **Agent:** `UsageCollector` đếm call thật theo `message_id`, lấy usage và cost từ `ResultMessage`; ghi usage cho OCR correction (qua Gateway job), plain / PEC chat, legacy run và delegate. **Gateway:** endpoint `/runtime/usage` cho call không có agent | Bật | Chỉ ghi nhận thêm |
| 2 Extraction | `/agent/document-extract`: system prompt chuyên biệt (policy nền tảng + công ty + hướng dẫn trích xuất); chỉ nạp skill **được prompt gọi tên** và đã được gán cho agent; không inline lại skill Gateway đã chèn; không tool | Bật (`EXTRACTION_LEAN_PROMPT`) | Bỏ skill không được yêu cầu; giữ đúng skill prompt chỉ định |
| 3 OCR | `ClaudeCliLlmService`: `tools=[]`; batch nhiều trang / call, có giới hạn ký tự và fallback từng trang khi tách trang sai | `tools=[]` bật; batch **tắt** (`OCR_CORRECTION_BATCH_CHARS=0`) | `tools=[]` không đổi hành vi; batch đổi cấu trúc prompt, cần so sánh chất lượng có trả phí |
| 4 Dynamic skill | Agent chat/run: inline skill declarative và `always_on`; catalog (tên + mô tả) cho skill còn lại; chọn trước theo keyword đưa vào user message; tool `load_skill` chỉ trả skill đã gán | **Tắt** (`DYNAMIC_SKILL_LOADING`) | Có thể làm mô hình bỏ sót hướng dẫn |
| 5 Legacy | `/agent/run` không agent ⇒ 403 trừ khi `LEGACY_AGENT_RUN_ENABLED=true`; sửa BFF chuyển agent đã chọn; giữ nguyên code `run_task` | Chặn | Đường không qua manifest / RBAC agent |
| 6 Context | History theo ngân sách ký tự (`HISTORY_MAX_CHARS`, `HISTORY_MAX_MESSAGES`) cho `/chat` và `/agent/run`; chỉ lưu câu trả lời cuối (sau tool call cuối) vào history; trần `max_turns` toàn cục | Bật | Câu trả lời trả cho người dùng không đổi |
| 7 Benchmark | Script offline đo cấu trúc prompt theo từng nghiệp vụ (byte thật, token ước lượng); script live (cần cho phép) đo token / cost / latency / accuracy trên bộ chứng từ tổng hợp; regression test | — | — |

## Kiểm thử sau mỗi phase

- Agent `pytest` (toàn bộ).
- Gateway test liên quan trên Postgres **cô lập**.
- Web `tsc` + `vitest` khi có sửa Web.

## Rollback

Chi tiết trong `TOKEN_OPTIMIZATION_RESULTS.md` §Rollback:
- tắt cờ cấu hình;
- migration 066 chỉ thêm cột nullable nên không cần gỡ;
- `git revert` khi cần.
