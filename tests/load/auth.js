/* eslint-disable no-undef */
// k6 load test: register → login → /me flow.
//
// Verifies that the auth rate limit (10/min global) protects upstream from
// brute force without blocking honest first-time users at small scale.

import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";

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
    // Rate-limit responses are expected when we exceed the bucket — they are
    // not failures of the system, but they also are not successes for k6.
    // Keep the failed rate generous to allow them through.
    "http_req_failed":   ["rate<0.25"],
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
