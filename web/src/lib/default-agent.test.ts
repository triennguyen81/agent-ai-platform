import { describe, expect, test } from "vitest";
import { landingPathFor, pickDefaultAgent } from "./default-agent";

const agent = (agentId: string, lifecycleState = "production", agentDisabled = false) => ({
  agentId,
  lifecycleState,
  agentDisabled,
});

describe("pickDefaultAgent", () => {
  test("keeps the last used agent while it is still usable", () => {
    expect(pickDefaultAgent([agent("a"), agent("b")], "b")).toBe("b");
  });

  test("falls back to the first usable agent when the cookie is missing, stale or no longer usable", () => {
    const agents = [agent("draft", "testing"), agent("off", "production", true), agent("a"), agent("b")];
    expect(pickDefaultAgent(agents, null)).toBe("a");
    expect(pickDefaultAgent(agents, "gone")).toBe("a");
    expect(pickDefaultAgent(agents, "off")).toBe("a");
    expect(pickDefaultAgent(agents, "draft")).toBe("a");
  });

  test("returns null when no agent is usable", () => {
    expect(pickDefaultAgent([], "a")).toBeNull();
    expect(pickDefaultAgent([agent("draft", "testing")], null)).toBeNull();
  });
});

describe("landingPathFor", () => {
  test("ADMIN and USER land on chat", () => {
    expect(landingPathFor(["admin"])).toBe("/chat");
    expect(landingPathFor(["user"])).toBe("/chat");
  });

  test("TENANT_OWNER and SUPER_ADMIN keep the dashboard", () => {
    expect(landingPathFor(["tenant_owner"])).toBe("/dashboard");
    expect(landingPathFor(["super_admin"])).toBe("/dashboard");
  });
});
