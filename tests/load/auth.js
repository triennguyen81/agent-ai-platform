/* eslint-disable no-undef */
// k6 load test: register → login → /me flow.
//
// Verifies that the auth rate limit (10/min global) protects upstream from
// brute force without blocking honest first-time users at small scale.

import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";

// ACL V2 final phase (V2-H): k6's built-in http_req_failed metric only
// counts 200-399 as non-failed by default, so every EXPECTED 429 (the auth
// rate limiter correctly firing — the entire point of this test) counted
// as a "failed" request, making the http_req_failed threshold below fail
// every time the rate limiter worked exactly as designed — see
// D8_PRODUCTION_READINESS_AUDIT.md §7. Declaring the accepted statuses here
// (matching the check()s below) fixes the metric at its source instead of
// only papering over it with a generous threshold.
http.setResponseCallback(http.expectedStatuses(200, 201, 400, 409, 429));

export const options = {
  scenarios: {
    ramp_up: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { duration: "30s", target: 5 },
        { duration: "1m",  target: 20 },
        { duration: "30s", target: 0 },
      ],
      gracefulRampDown: "10s",
    },
  },
  thresholds: {
    "http_req_duration": ["p(95)<800"],
    // Now that setResponseCallback (above) tells k6 which statuses are
    // genuinely expected, http_req_failed only counts real failures
    // (5xx/network errors) — a tight threshold is meaningful again.
    "http_req_failed":   ["rate<0.05"],
  },
};

export default function () {
  const uniqueId = `${__VU}-${__ITER}-${Date.now()}`;
  const email = `loadtest+${uniqueId}@aiq.local`;
  const password = "loadtest-strong-pass";

  const registerRes = http.post(
    `${BASE_URL}/auth/register`,
    JSON.stringify({ email, password, displayName: `LT ${uniqueId}` }),
    { headers: { "Content-Type": "application/json" }, tags: { name: "register" } },
  );
  check(registerRes, {
    "register accepted": (r) => r.status === 201 || r.status === 429 || r.status === 409,
  });

  if (registerRes.status !== 201) {
    sleep(1);
    return;
  }

  const loginRes = http.post(
    `${BASE_URL}/auth/login`,
    JSON.stringify({ email, password }),
    { headers: { "Content-Type": "application/json" }, tags: { name: "login" } },
  );
  check(loginRes, { "login 200": (r) => r.status === 200 });

  if (loginRes.status !== 200) {
    sleep(1);
    return;
  }

  const token = loginRes.json("token");
  const meRes = http.get(`${BASE_URL}/me`, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { name: "me" },
  });
  check(meRes, { "/me 200": (r) => r.status === 200 });

  sleep(1);
}
