# Skills Authoring Guide

> Cách viết một SKILL.md mới cho AIQ. Đọc 10 phút, viết skill mới trong 15 phút.
>
> Audience: developer, kế toán viên, bác sĩ, sales — bất kỳ ai có domain knowledge muốn đóng gói thành quy ước cho AI.

---

## 1. Skill là gì

**Skill = một thư mục chứa file `SKILL.md`** với 2 phần:

```
agent/skills/<skill-name>/SKILL.md
```

- **Frontmatter (YAML)** ở đầu file: metadata (`name`, `description`, `keywords`, ...)
- **Body (Markdown)** sau frontmatter: nội dung quy ước thật — tone, format, citation, từ vựng chuyên ngành

Khi user chat hoặc gọi `/agent/run`, **skill loader** đọc các SKILL.md, quyết định skill nào hoạt động, rồi inject body vào system prompt của Claude. Claude vẫn là model gốc; skill chỉ là instructions thêm.

---

## 2. Frontmatter format

```markdown
---
name: dental-expert
description: |
  Mô tả 1-3 câu khi nào kích hoạt skill này. Đây là chỉ dẫn cho team đọc,
  không phải prompt — đừng viết dài.
keywords:
  - implant
  - straumann
  - nha khoa
  - "tẩy trắng"
always_on: false
when:
  - has_knowledge_context
---
```

### Các trường

| Trường | Bắt buộc | Ý nghĩa |
|---|---|---|
| `name` | có | Tên unique (kebab-case). Phải khớp tên thư mục. |
| `description` | nên | 1-3 câu giải thích cho team — không inject vào prompt |
| `keywords` | tuỳ | Danh sách từ khoá (lowercase). Skill kích hoạt khi user prompt chứa **bất kỳ** keyword nào (substring case-insensitive) |
| `always_on` | tuỳ | `true` = mọi turn đều active (vd `vietnamese-style`) |
| `when` | tuỳ | List điều kiện thêm. Hỗ trợ: `has_knowledge_context` (chỉ active khi RAG có Note) |

### Selection rules (tự động)

Loader chọn skill cho từng turn theo thứ tự ưu tiên:

1. **`always_on: true`** → luôn included (vd ngôn ngữ, tone chung)
2. **`when: [has_knowledge_context]`** → chỉ included nếu turn này có Note bổ sung
3. **`keywords`** → match substring trên user prompt → included

Không match cả 3 → skill **bị bỏ qua**.

---

## 3. Body — viết gì?

Body là **instructions Claude sẽ đọc**. Hãy viết như đang dạy việc cho nhân viên mới:

### Cấu trúc đề xuất

```markdown
# {Tên skill}

{1 câu định vị: bạn đóng vai gì khi skill này active}

## Nguyên tắc trả lời
- {Rule 1}
- {Rule 2}

## Format câu trả lời
1. {Bước 1}
2. {Bước 2}

## Từ vựng chuyên ngành
| Tránh | Dùng |
|---|---|
| Tiếng Anh thô | Việt hoá đúng |

## Khi không có thông tin
{Cách reply khi user hỏi câu skill không cover được}

## Tránh
- {Anti-pattern 1}
- {Anti-pattern 2}
```

### Mẹo viết hiệu quả

- **Cụ thể hơn trừu tượng:** "Tên TK ghi `TK 156` không `tài khoản 156`" tốt hơn "Dùng terminology đúng"
- **Đưa ví dụ phản chứng:** "Tránh: 'Bạn muốn biết về Straumann nào?' → Dùng: 'Anh/chị quan tâm SLActive hay Roxolid?'"
- **Format bằng Markdown:** Claude render Markdown rất tốt; dùng bảng / bullet / code block để nén nhiều rule
- **Đừng nhồi quá nhiều:** 1 skill = 1 domain. Tách `accounting-vn-style` khác `tax-calculation` khác `journal-entry-formatter`

### Độ dài lý tưởng

- 50-300 dòng Markdown
- Quá ngắn (<30 dòng): có thể merge với skill khác
- Quá dài (>500 dòng): có thể tách thành 2 skill con

---

## 4. 4 mẫu skill có sẵn (đọc để học)

| Skill | Loại | Học gì từ đó |
|---|---|---|
| [`vietnamese-style`](../agent/skills/vietnamese-style/SKILL.md) | `always_on: true` | Cách viết quy ước ngôn ngữ áp dụng mọi câu trả lời |
| [`dental-expert`](../agent/skills/dental-expert/SKILL.md) | `keywords` | Cách map từ khoá domain → kích hoạt skill chuyên ngành |
| [`rag-citation`](../agent/skills/rag-citation/SKILL.md) | `when: [has_knowledge_context]` | Cách chỉ kích hoạt khi có context bổ sung |
| [`url-ingest`](../agent/skills/url-ingest/SKILL.md) | `keywords` + agentic | Cách viết skill cho `/agent/run` (multi-step tool use) |

---

## 5. Quy trình viết skill mới (5 bước)

### Bước 1 — Quyết định scope

Trả lời 3 câu:

- Skill này áp dụng **khi nào**? (luôn / domain keyword / có RAG)
- **Một câu** mô tả skill này dạy AI điều gì?
- Có **xung đột** với skill nào đang có không? Nếu có → merge hay tách?

### Bước 2 — Tạo thư mục

```bash
mkdir -p agent/skills/<skill-name>
touch agent/skills/<skill-name>/SKILL.md
```

Tên thư mục = `name` trong frontmatter, kebab-case (vd `accounting-vn-style`, không `Accounting_VN_Style`).

### Bước 3 — Viết SKILL.md

Copy template từ section 2 + section 3. Viết frontmatter rồi body.

### Bước 4 — Test selection logic local

Sau khi push + deploy, gọi smoke test trong container:

```bash
ssh contabo-cosmos "sudo -n docker exec aiq-agent python -c \"
import sys; sys.path.insert(0, '/app/skills/..')
from pathlib import Path
from aiq_agent.skills import build_registry, select_skills
reg = build_registry(Path('/app/skills'))
# Test các prompt khác nhau xem skill có kích hoạt đúng không
print(select_skills(reg, prompt='câu hỏi mẫu của domain', has_knowledge_context=False))
\""
```

### Bước 5 — Test live qua chat

Mở `https://app.cattuong.ai/chat`, hỏi câu thuộc domain → kiểm tra câu trả lời có:

- Đúng tone / format quy định?
- Citation đúng?
- Không xung đột với skill khác?

Nếu không khớp → quay lại bước 3, tinh chỉnh.

---

## 6. Skill cho `/agent/run` (agentic mode)

Khi viết skill cho task multi-step, body nên có:

- **Step-by-step workflow** rõ ràng — gọi tool nào, theo thứ tự nào
- **Output format** cho user thấy (vd "Trả về xác nhận: 'Đã lưu N Note: ...'")
- **Lỗi xử lý** — fallback khi tool fail
- **Giới hạn** — cap size, không gọi tool quá nhiều lần

Xem `url-ingest/SKILL.md` để học pattern.

---

## 7. Danh sách tools sẵn có cho agentic skill

Khi skill được activate trong `/agent/run`, model có thể gọi:

| Tool | Mô tả | Skill nên dùng |
|---|---|---|
| `mcp__aiq__search_notes` | Tìm note theo query | knowledge-curator, url-ingest |
| `mcp__aiq__get_note` | Lấy 1 note đầy đủ | bất kỳ skill cần đọc note cụ thể |
| `mcp__aiq__save_note` | Lưu note mới | url-ingest, knowledge-curator |
| `mcp__aiq__fetch_url` | Fetch URL (cap 256 KB) | url-ingest, research |

Tools mới sẽ được wire trong [tools.py](../agent/src/aiq_agent/agentic/tools.py).

---

## 8. Anti-patterns thường gặp

| Sai | Sửa |
|---|---|
| Skill name trùng tên thư mục cha (`/app/skills/dental` + `name: dental-expert`) | Tên thư mục = `name` chính xác |
| Body chỉ có "Be helpful" / "Be professional" | Body phải có quy ước cụ thể (format / từ vựng / citation) |
| Keywords chứa unicode escaped (`í`) | Viết keyword bằng UTF-8 plain |
| Skill 800 dòng | Tách thành 2-3 skill con |
| 2 skill `always_on` xung đột về tone | Chỉ 1 skill `always_on` cho tone — skill khác dùng `keywords` |
| Skill có data nhạy cảm (lương, secret) | KHÔNG ghi vào SKILL.md — skills nằm trong git public; data nên ở Notes |

---

## 9. Khi nào KHÔNG cần viết skill

- Câu hỏi 1 lần / use case ngắn hạn → trực tiếp prompt là đủ
- Knowledge cụ thể (số liệu, fact) → để vào **Notes** (KB), không phải skill
- Tính năng cần code thật (tính toán, gọi API) → wire vào `tools.py` chứ không phải skill

Quy tắc: **skill = HOW (cách trả lời)**, **note = WHAT (nội dung trả lời)**, **tool = DO (thực thi hành động)**.

---

## 10. Roadmap skill system

- **M-Skills** ✅ — Phase 1-3 done (loader + 5 skills + agentic)
- **M-Skills-2** 🔜 — UI để bật/tắt skill từ `/admin/skills` (cho super_admin)
- **M-Skills-3** 🔜 — Skill versioning (track diff khi sửa SKILL.md)
- **M-Skills-4** 🔜 — A/B test 2 phiên bản skill song song
- **M-Skills-5** 🔜 — Skill marketplace (browse skills từ open repo của community)

---

## 11. References

- [Loader implementation](../agent/src/aiq_agent/skills/loader.py)
- [Active skills directory](../agent/skills/)
- [Anthropic Claude Agent SDK — Skills docs](https://github.com/anthropics/claude-agent-sdk-python)
- [Strategy B plan](claude_agent_sdk_integration_plan.md)
