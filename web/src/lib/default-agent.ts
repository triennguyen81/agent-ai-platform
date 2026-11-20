// Where a signed-in viewer lands, and which agent /chat opens when the URL
// names none. Pure: callers fetch /me/agents (already ordered by name by the
// Gateway) and read the active-agent cookie themselves.

export type AgentChoice = {
  agentId: string;
  lifecycleState: string;
  agentDisabled: boolean;
};

export function isUsableAgent(agent: AgentChoice): boolean {
  return agent.lifecycleState === "production" && !agent.agentDisabled;
}

/**
 * The agent /chat opens by default: the last one used (cookie) while it is
 * still usable, otherwise the first usable one in list order — the same
 * fallback the sidebar highlights. Null when the viewer has no usable agent.
 */
export function pickDefaultAgent(agents: AgentChoice[], cookieAgentId: string | null): string | null {
  const usable = agents.filter(isUsableAgent);
  if (cookieAgentId && usable.some((a) => a.agentId === cookieAgentId)) return cookieAgentId;
  return usable[0]?.agentId ?? null;
}

/** SUPER_ADMIN / TENANT_OWNER have no chat and keep the dashboard; ADMIN / USER go straight to chat. */
export function landingPathFor(roles: string[]): "/dashboard" | "/chat" {
  return roles.includes("super_admin") || roles.includes("tenant_owner") ? "/dashboard" : "/chat";
}
