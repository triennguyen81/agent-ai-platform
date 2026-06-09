/* eslint-disable no-undef */
// k6 load test: paginated book listing + per-book reads.
//
// Pass BEARER_TOKEN from an already-logged-in user. The script generates
// per-VU search queries so we stress the ILIKE filter path.

import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";
const TOKEN = __ENV.BEARER_TOKEN;

if (!TOKEN) {
  throw new Error("BEARER_TOKEN env var is required (login a user first)");
}

const HEADERS = {
  Authorization: `Bearer ${TOKEN}`,
  "Content-Type": "application/json",
};

export const options = {
  scenarios: {
    sustained: {
      executor: "constant-vus",
      vus: 25,
      duration: "2m",
    },
  },
  thresholds: {
    "http_req_duration{name:list}":  ["p(95)<400"],
    "http_req_duration{name:read}":  ["p(95)<300"],
    "http_req_failed":               ["rate<0.05"],
  },
};

export default function () {
  const query = ["kinh", "thanh", "phuc", "am", "thu"][__ITER % 5];

  const listRes = http.get(
    `${BASE_URL}/books?q=${query}&limit=20`,
    { headers: HEADERS, tags: { name: "list" } },
  );
  check(listRes, { "list 200": (r) => r.status === 200 });

  if (listRes.status === 200) {
    const items = listRes.json("items") || [];
    if (items.length > 0) {
      const id = items[Math.floor(Math.random() * items.length)].id;
      const detailRes = http.get(
        `${BASE_URL}/books/${id}`,
        { headers: HEADERS, tags: { name: "read" } },
      );
      check(detailRes, { "read 200": (r) => r.status === 200 });
    }
  }

  sleep(1);
}
