/* eslint-disable no-undef */
// k6 load test: Agent /chat — exercises PEC orchestrator + memory store.
//
// Hits the Agent directly (not via Gateway) so we measure orchestration
// latency without bearer-token verification overhead in the way. Expect
// the LLM provider (Ollama or Anthropic) to dominate wall time.

import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8001";

export const options = {
  scenarios: {
    warm: {
      executor: "constant-vus",
      vus: 4,                     // LLM concurrency is the real bottleneck
      duration: "3m",
      startTime: "30s",
    },
  },
  thresholds: {
    "http_req_duration": ["p(95)<30000"],   // generous: LLM chains can be slow
    "http_req_failed":   ["rate<0.05"],
  },
};

const PROMPTS = [
  "Hãy tóm tắt ý chính câu sau: Phúc thay ai có tâm hồn nghèo khó.",
  "Sửa dấu tiếng Việt: Cong cha nhu nui Thai Son.",
  "Kiểm tra ngữ pháp: Toi muon di hoc ngay mai.",
];

export default function () {
  const threadId = `loadtest-${__VU}`;
  const prompt = PROMPTS[__ITER % PROMPTS.length];

  const res = http.post(
    `${BASE_URL}/chat`,
    JSON.stringify({
      prompt,
      thread_id: threadId,
      temperature: 0.2,
    }),
    {
      headers: { "Content-Type": "application/json" },
      timeout: "60s",
    },
  );
  check(res, {
    "chat 200": (r) => r.status === 200,
    "non-empty answer": (r) => {
      try { return (r.json("final_answer") || "").length > 0; } catch { return false; }
    },
  });

  sleep(2);
}
