import { beforeEach, expect, test, vi } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/auth", () => ({
  getSession: vi.fn(),
  getActiveAgent: vi.fn(),
  namespacedThreadId: vi.fn((_token: string, id?: string) => id),
}));
vi.mock("@/lib/api", () => ({
  agent: { runTask: vi.fn() },
  gateway: { listMyAgents: vi.fn() },
}));
import { getActiveAgent, getSession } from "@/lib/auth";
import { agent, gateway } from "@/lib/api";
import { POST } from "./route";

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getSession).mockResolvedValue("token");
  vi.mocked(agent.runTask).mockResolvedValue(new Response("{}", { status: 200 }));
});

function run(body: Record<string, unknown>): Promise<Response> {
  return POST(new NextRequest("http://web/api/agent/run", { method: "POST", body: JSON.stringify(body) }));
}

test("forwards the active agent when the caller sent none (never the no-agent runtime)", async () => {
  vi.mocked(getActiveAgent).mockResolvedValue("agent-active");
  await run({ task: "Công nợ khách hàng A?" });
  expect(agent.runTask).toHaveBeenCalledWith("token", expect.objectContaining({ agent_id: "agent-active" }));
});

test("falls back to the first assigned agent when no active agent is set", async () => {
  vi.mocked(getActiveAgent).mockResolvedValue(undefined as unknown as string);
  vi.mocked(gateway.listMyAgents).mockResolvedValue(
    new Response(JSON.stringify([{ id: "agent-first" }]), { status: 200 }),
  );
  await run({ task: "x" });
  expect(agent.runTask).toHaveBeenCalledWith("token", expect.objectContaining({ agent_id: "agent-first" }));
});

test("an explicit agent_id wins", async () => {
  vi.mocked(getActiveAgent).mockResolvedValue("agent-active");
  await run({ task: "x", agent_id: "agent-explicit" });
  expect(agent.runTask).toHaveBeenCalledWith("token", expect.objectContaining({ agent_id: "agent-explicit" }));
});

test("a capability request never also carries the fallback agent_id", async () => {
  vi.mocked(getActiveAgent).mockResolvedValue("agent-active");
  await run({ task: "x", capability: "accounting.tax" });
  const payload = vi.mocked(agent.runTask).mock.calls[0][1];
  expect(payload.capability).toBe("accounting.tax");
  expect(payload.agent_id).toBeUndefined();
});
