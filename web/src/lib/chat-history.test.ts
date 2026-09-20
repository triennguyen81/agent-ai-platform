import { describe, expect, it } from "vitest";
import {
  filterThreadsByAgent,
  filterThreadsByMode,
  modeForHistoryItem,
  normalizeMode,
  storageKeyForAgentAndMode,
  storageKeyForMode,
  threadBelongsToAgent,
} from "./chat-history";

describe("normalizeMode", () => {
  it("returns agentic only for the exact literal 'agentic'", () => {
    expect(normalizeMode("agentic")).toBe("agentic");
  });

  it("defaults 'chat' to chat", () => {
    expect(normalizeMode("chat")).toBe("chat");
  });

  it("defaults legacy threads with no mode field to chat (backward compat)", () => {
    expect(normalizeMode(null)).toBe("chat");
    expect(normalizeMode(undefined)).toBe("chat");
  });

  it("defaults any unrecognized/malformed value to chat, never guesses", () => {
    expect(normalizeMode("")).toBe("chat");
    expect(normalizeMode("Agentic")).toBe("chat"); // case-sensitive, no fuzzy match
    expect(normalizeMode("something-else")).toBe("chat");
  });
});

describe("storageKeyForMode", () => {
  it("uses distinct keys per mode so the two never collide", () => {
    expect(storageKeyForMode("chat")).not.toBe(storageKeyForMode("agentic"));
  });

  it("keeps the pre-existing chat key unchanged for backward compatibility", () => {
    expect(storageKeyForMode("chat")).toBe("aiq.chat.thread_id");
  });
});

describe("filterThreadsByMode — Chat/Agentic history filtering", () => {
  const threads = [
    { thread_id: "c1", mode: "chat" as const },
    { thread_id: "c2", mode: "chat" as const },
    { thread_id: "a1", mode: "agentic" as const },
  ];

  it("Chat mode: returns only Chat threads", () => {
    const result = filterThreadsByMode(threads, "chat");
    expect(result.map((t) => t.thread_id)).toEqual(["c1", "c2"]);
  });

  it("Agentic mode: returns only Agentic threads", () => {
    const result = filterThreadsByMode(threads, "agentic");
    expect(result.map((t) => t.thread_id)).toEqual(["a1"]);
  });

  it("never mixes modes — every item in a Chat-filtered result is mode=chat", () => {
    const result = filterThreadsByMode(threads, "chat");
    expect(result.every((t) => t.mode === "chat")).toBe(true);
  });

  it("never mixes modes — every item in an Agentic-filtered result is mode=agentic", () => {
    const result = filterThreadsByMode(threads, "agentic");
    expect(result.every((t) => t.mode === "agentic")).toBe(true);
  });

  it("treats a legacy thread with no mode field as Chat, not Agentic", () => {
    const legacy = [{ thread_id: "old-1", mode: undefined as unknown as string }];
    expect(filterThreadsByMode(legacy, "chat").map((t) => t.thread_id)).toEqual(["old-1"]);
    expect(filterThreadsByMode(legacy, "agentic")).toEqual([]);
  });
});

describe("modeForHistoryItem — restoring mode when opening a history item", () => {
  it("restores agentic when the clicked item is an agentic thread", () => {
    expect(modeForHistoryItem({ mode: "agentic" })).toBe("agentic");
  });

  it("restores chat when the clicked item is a chat thread", () => {
    expect(modeForHistoryItem({ mode: "chat" })).toBe("chat");
  });

  it("never infers mode from anything other than the item's own persisted field", () => {
    // Even a thread with a title that LOOKS agentic ("agent task...") must
    // still resolve purely from its own `mode` field.
    expect(modeForHistoryItem({ mode: "chat", title: "agent task 1" } as never)).toBe("chat");
  });
});

describe("storageKeyForAgentAndMode — agent-scoped localStorage keys", () => {
  it("gives different agents distinct keys for the same mode", () => {
    expect(storageKeyForAgentAndMode("agent-zzz", "chat")).not.toBe(
      storageKeyForAgentAndMode("agent-accounting", "chat"),
    );
  });

  it("gives the same agent distinct keys per mode", () => {
    expect(storageKeyForAgentAndMode("agent-zzz", "chat")).not.toBe(
      storageKeyForAgentAndMode("agent-zzz", "agentic"),
    );
  });

  it("is stable/deterministic for the same (agent, mode) pair", () => {
    expect(storageKeyForAgentAndMode("agent-zzz", "chat")).toBe(
      storageKeyForAgentAndMode("agent-zzz", "chat"),
    );
  });

  it("gives the no-agent case its own bucket, distinct from any real agent id", () => {
    const noAgent = storageKeyForAgentAndMode(null, "chat");
    const realAgent = storageKeyForAgentAndMode("_none", "chat"); // adversarial id
    expect(noAgent).toBe(storageKeyForAgentAndMode(undefined, "chat"));
    expect(noAgent).toBe(realAgent); // documents the sentinel collision risk is accepted
  });
});

describe("filterThreadsByAgent — Chat/Agentic history scoped by agent_id", () => {
  const threads = [
    { thread_id: "z1", agent_id: "agent-zzz" },
    { thread_id: "z2", agent_id: "agent-zzz" },
    { thread_id: "a1", agent_id: "agent-accounting" },
    { thread_id: "legacy-1", agent_id: null },
  ];

  it("returns only the selected agent's threads", () => {
    expect(filterThreadsByAgent(threads, "agent-zzz").map((t) => t.thread_id)).toEqual([
      "z1",
      "z2",
    ]);
  });

  it("never mixes agents", () => {
    const result = filterThreadsByAgent(threads, "agent-accounting");
    expect(result.every((t) => t.agent_id === "agent-accounting")).toBe(true);
  });

  it("legacy threads without agent_id never attach to a specific agent filter", () => {
    expect(filterThreadsByAgent(threads, "agent-zzz")).not.toContainEqual(
      expect.objectContaining({ thread_id: "legacy-1" }),
    );
  });

  it("no-agent filter (null) returns only legacy/no-agent threads, not every agent's", () => {
    expect(filterThreadsByAgent(threads, null).map((t) => t.thread_id)).toEqual(["legacy-1"]);
  });
});

describe("threadBelongsToAgent — guards opening a cross-agent thread", () => {
  it("true when the thread's agent_id matches the current agent exactly", () => {
    expect(threadBelongsToAgent({ agent_id: "agent-zzz" }, "agent-zzz")).toBe(true);
  });

  it("false when the thread belongs to a different agent", () => {
    expect(threadBelongsToAgent({ agent_id: "agent-accounting" }, "agent-zzz")).toBe(false);
  });

  it("false when a legacy (no-agent) thread is opened under a selected agent", () => {
    expect(threadBelongsToAgent({ agent_id: null }, "agent-zzz")).toBe(false);
  });

  it("true when both the thread and the current selection have no agent", () => {
    expect(threadBelongsToAgent({ agent_id: null }, null)).toBe(true);
    expect(threadBelongsToAgent({ agent_id: undefined }, undefined)).toBe(true);
  });
});
