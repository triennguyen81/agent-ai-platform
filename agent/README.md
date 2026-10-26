# aiq-agent

Agent runtime sublayer của AIQ Agentic AI Platform. Python 3.12 + FastAPI, hỗ trợ Qwen3 (Ollama/MLX) và Claude.

Xem repo root [README](../README.md) để biết tổng thể stack.

## Layout

```
agent/
├── pyproject.toml
├── Dockerfile
├── src/aiq_agent/
│   ├── main.py            # FastAPI app composition
│   ├── config.py          # pydantic-settings
│   ├── llm/               # provider abstraction (Ollama, Anthropic)
│   ├── tools/             # function-calling registry
│   ├── orchestration/     # Planner-Executor-Critic
│   ├── ocr/               # PDF → PaddleOCR → diacritic correction → pgvector validate
│   └── api/               # /healthz, /chat, /tools, /ocr/pdf
└── tests/                 # pytest-asyncio unit tests (StubOcrExtractor + FakeLlmService)
```

## Install & test

Requires Python 3.12+. Optional dependency groups:

```bash
# Base service only
pip install .

# + OCR (PaddleOCR, pypdfium2, Pillow — heavy)
pip install '.[ocr]'

# + pgvector validation (asyncpg + pgvector)
pip install '.[pgvector]'

# + dev (pytest, ruff)
pip install '.[dev]'

# Run the pipeline unit tests (no heavy deps required)
pytest tests/

# Lint exactly as CI does (ruff is pinned in the dev extra)
ruff check src tests
```

## OCR pipeline smoke run

```bash
curl -X POST http://localhost:8001/ocr/pdf \
  -F file=@sample.pdf
```

Returns per-page raw OCR text, LLM-corrected text, confidence, similarity (if pgvector wired), and stage timings.
