# aiq-agent

Agent runtime sublayer của AIQ Agentic AI Platform. Python 3.12 + FastAPI, hỗ trợ Qwen3 (Ollama/MLX) và Claude.

Xem repo root [README](../README.md) để biết tổng thể stack.

## Layout

```
agent/
├── pyproject.toml
├── Dockerfile
└── src/aiq_agent/
    ├── main.py            # FastAPI app composition
    ├── config.py          # pydantic-settings
    ├── llm/               # provider abstraction (Ollama, Anthropic)
    ├── tools/             # function-calling registry
    ├── orchestration/     # Planner-Executor-Critic
    └── api/               # /healthz, /chat
```
