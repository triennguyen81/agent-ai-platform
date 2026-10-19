import { describe, expect, test } from "vitest";
import { contractSchedulesOf, scheduleLabel, scheduleRemaining, scheduleStatusLabel } from "./contract-schedule";

describe("contract schedule view", () => {
  test("status labels map the Gateway's canonical schedule statuses", () => {
    expect(scheduleStatusLabel("pending")).toBe("CHƯA THANH TOÁN");
    expect(scheduleStatusLabel("partial")).toBe("THANH TOÁN MỘT PHẦN");
    expect(scheduleStatusLabel("paid")).toBe("ĐÃ THANH TOÁN");
    expect(scheduleStatusLabel("weird")).toBe("weird");
  });

  test("remaining amount comes from Gateway totals and never goes negative", () => {
    expect(scheduleRemaining({ scheduleTotal: 40_000_000, paidAmount: 20_000_000 })).toBe(20_000_000);
    expect(scheduleRemaining({ scheduleTotal: 40_000_000, paidAmount: 45_000_000 })).toBe(0);
  });

  test("no contract block without explicit schedule links (older responses included)", () => {
    expect(contractSchedulesOf(null)).toEqual([]);
    expect(contractSchedulesOf({})).toEqual([]);
    expect(contractSchedulesOf({ contractSchedules: [] })).toEqual([]);
  });

  test("schedule label never repeats the đợt number", () => {
    expect(scheduleLabel({ ordinal: 1, description: "Đợt 1 — tạm ứng 40%" })).toBe("Đợt 1 — tạm ứng 40%");
    expect(scheduleLabel({ ordinal: 2, description: "Nghiệm thu" })).toBe("Đợt 2 — Nghiệm thu");
    expect(scheduleLabel({ ordinal: 3, description: "" })).toBe("Đợt 3");
  });
});
