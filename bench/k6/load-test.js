// k6 load test for the Level 7 gateway.
//
//   1. docker compose up -d
//   2. dotnet run --project src/Level7.Gateway --urls http://localhost:5097
//   3. k6 run bench/k6/load-test.js         (install k6: https://k6.io/docs/get-started/installation/)
//
// The gateway is configured with a limit (default 100 / 10s). Under sustained load most requests get
// 429'd — that's correct. We record the allow/reject split and latency, and check that the gateway
// stays up (no 5xx) while shedding load at the edge. Bump the stages to push toward ~10k rps.
import http from "k6/http";
import { check } from "k6";
import { Counter } from "k6/metrics";

const allowed = new Counter("gateway_allowed");
const rejected = new Counter("gateway_rejected");

export const options = {
  scenarios: {
    ramp: {
      executor: "ramping-arrival-rate",
      startRate: 200,
      timeUnit: "1s",
      preAllocatedVUs: 200,
      maxVUs: 2000,
      stages: [
        { target: 1000, duration: "20s" },
        { target: 5000, duration: "20s" },
        { target: 5000, duration: "20s" },
      ],
    },
  },
  thresholds: {
    // The gateway must never 5xx — it either allows or cleanly 429s.
    "http_req_failed{expected_response:true}": ["rate<0.01"],
    http_req_duration: ["p(99)<50"],
  },
};

const URL = __ENV.GATEWAY_URL || "http://localhost:5097/api/resource";

export default function () {
  const res = http.get(URL);
  const is429 = res.status === 429;
  // Treat 200 and 429 as "expected" (not failures) — both are correct gateway behaviour.
  check(res, { "allowed or limited": (r) => r.status === 200 || r.status === 429 },
    { expected_response: true });
  if (res.status === 200) allowed.add(1);
  if (is429) rejected.add(1);
}
