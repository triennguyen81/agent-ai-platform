# Load tests (k6)

Scripts in this directory drive realistic traffic against the Gateway + Agent
so we can tune the system before promotion.

## Run

Install k6 once: <https://grafana.com/docs/k6/latest/get-started/installation/>

```bash
# Auth flow (register → login → /me) — checks rate limit + auth path
k6 run -e BASE_URL=http://localhost:8080 tests/load/auth.js

# Books CRUD (list + read) — checks per-user rate limit + paginated queries
k6 run -e BASE_URL=http://localhost:8080 -e BEARER_TOKEN="$TOKEN" tests/load/books.js

# Agent chat — checks LLM throughput; expect Ollama to be the bottleneck
k6 run -e BASE_URL=http://localhost:8001 tests/load/agent_chat.js
```

Each script targets the GOLDEN PATH only (200/201/202). Errors fail the run.

## Thresholds

All scripts set p(95) thresholds. If they fail, the load test is considered a
regression and the build is rejected. Tune `Aiq.Gateway` rate-limit policies
or `OcrJobScheduler` worker counts as needed — never relax thresholds without
documenting why.

## Reading the output

| Metric | What it means |
|--------|---------------|
| `http_req_duration{p(95)}` | 95th percentile request latency |
| `http_req_failed{rate}` | Non-2xx response rate |
| `iterations` | Number of full VU cycles completed |
| `vus` | Concurrent virtual users at any time |
