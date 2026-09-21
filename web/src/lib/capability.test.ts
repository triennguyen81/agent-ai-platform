import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchCapabilityAgentSelection } from "./capability";

function mockFetchOnce(status: number, body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("fetchCapabilityAgentSelection", () => {
  it("returns 'none' when zero agents are eligible", async () => {
    mockFetchOnce(200, { items: [] });
    const result = await fetchCapabilityAgentSelection("contract.processing");
    expect(result).toEqual({ status: "none" });
  });

  it("auto-selects when exactly one agent is eligible", async () => {
    mockFetchOnce(200, { items: [{ agentId: "a1", slug: "contract-agent", name: "Contract Agent" }] });
    const result = await fetchCapabilityAgentSelection("contract.processing");
    expect(result).toEqual({
      status: "auto",
      agent: { agentId: "a1", slug: "contract-agent", name: "Contract Agent" },
    });
  });

  it("returns 'choose' with no default selection when more than one agent is eligible", async () => {
    mockFetchOnce(200, {
      items: [
        { agentId: "a1", slug: "contract-agent", name: "Contract Agent" },
        { agentId: "a2", slug: "contract-agent-2", name: "Contract Agent 2" },
      ],
    });
    const result = await fetchCapabilityAgentSelection("contract.processing");
    expect(result.status).toBe("choose");
    if (result.status === "choose") {
      expect(result.agents).toHaveLength(2);
      expect(result.selectedAgentId).toBeNull();
    }
  });

  it("preselects the ?agent= id only when it is itself eligible", async () => {
    mockFetchOnce(200, {
      items: [
        { agentId: "a1", slug: "contract-agent", name: "Contract Agent" },
        { agentId: "a2", slug: "contract-agent-2", name: "Contract Agent 2" },
      ],
    });
    const result = await fetchCapabilityAgentSelection("contract.processing", "a2");
    expect(result.status).toBe("choose");
    if (result.status === "choose") expect(result.selectedAgentId).toBe("a2");
  });

  it("ignores a preselect id that is not in the eligible set — never a bypass", async () => {
    mockFetchOnce(200, {
      items: [
        { agentId: "a1", slug: "contract-agent", name: "Contract Agent" },
        { agentId: "a2", slug: "contract-agent-2", name: "Contract Agent 2" },
      ],
    });
    const result = await fetchCapabilityAgentSelection("contract.processing", "not-eligible-agent");
    expect(result.status).toBe("choose");
    if (result.status === "choose") expect(result.selectedAgentId).toBeNull();
  });

  it("returns 'error' when the request fails, never fabricates an agent", async () => {
    mockFetchOnce(500, {});
    const result = await fetchCapabilityAgentSelection("contract.processing");
    expect(result).toEqual({ status: "error" });
  });
});
