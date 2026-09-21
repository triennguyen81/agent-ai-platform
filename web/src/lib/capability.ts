// D6 canonical Web helper: every AI-assisted business page routes through
// this one function instead of duplicating "call /agent/run, handle 403/409"
// logic per page. It only ever sends a stable capability code — never a
// tenant_id, department_id, or agent_id — because Web cannot prove
// authorization; the Gateway's resolver (D4) is the sole authority on
// tenant/department/role/entitlement/binding/ambiguity. This function does
// not, and must not, duplicate any of that eligibility logic.
export type CapabilityErrorReason = "unavailable" | "ambiguous" | "unknown";

export class CapabilityError extends Error {
  constructor(
    public reason: CapabilityErrorReason,
    message: string,
  ) {
    super(message);
    this.name = "CapabilityError";
  }
}

/**
 * Runs a business AI task through the capability resolver. `capability`
 * must be a stable catalog code (e.g. "accounting.journal") — the browser
 * never picks, knows, or sends an agent id. `preferredAgentId` is only for
 * pages using useCapabilityAgentSelection below (the "Xử lý bằng trợ lý"
 * selector) — the Gateway re-validates it fresh before ever honoring it,
 * exactly like it does for a raw agent_id.
 */
export async function executeCapability(
  capability: string,
  task: string,
  maxTurns = 4,
  preferredAgentId?: string,
): Promise<string> {
  const res = await fetch("/api/agent/run", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      task,
      max_turns: maxTurns,
      capability,
      preferred_agent_id: preferredAgentId,
    }),
  });

  if (res.status === 409) {
    throw new CapabilityError(
      "ambiguous",
      "Nghiệp vụ này đang được gán cho nhiều Agent. Vui lòng liên hệ quản trị để cấu hình rõ ràng.",
    );
  }
  if (res.status === 403) {
    throw new CapabilityError(
      "unavailable",
      "Bạn chưa được cấp Agent hỗ trợ nghiệp vụ này. Liên hệ admin/tenant owner để được cấu hình.",
    );
  }
  if (!res.ok) {
    // Deliberately generic — never surface response bodies verbatim here,
    // they may carry internal detail (stack traces, SQL, upstream errors).
    throw new CapabilityError("unknown", `Đã có lỗi xảy ra (HTTP ${res.status}). Vui lòng thử lại.`);
  }

  const body = (await res.json()) as { final_answer: string };
  return body.final_answer;
}

// ---------------------------------------------------------------------
// "Xử lý bằng trợ lý" agent-selection helper — the reusable routing piece
// this was built for (Contract first; Voucher/Invoice/Customer/Supplier/
// Expense reuse it unchanged by just passing their own capability code).
// Never an authorization decision on its own: it only decides what the UI
// shows (nothing / auto-selected / a picker). Whatever ends up selected
// still has to pass the Gateway's own preferredAgentId re-validation at
// submit time — this module cannot forge that outcome.
// ---------------------------------------------------------------------

export interface EligibleCapabilityAgent {
  agentId: string;
  slug: string;
  name: string;
}

export type CapabilityAgentSelectionState =
  | { status: "loading" }
  | { status: "error" }
  | { status: "none" }
  | { status: "auto"; agent: EligibleCapabilityAgent }
  | { status: "choose"; agents: EligibleCapabilityAgent[]; selectedAgentId: string | null };

/**
 * Fetches the eligible-agent list for `capability` and classifies it into
 * 0 / 1 / many. `preselectAgentId` (e.g. the page was opened with
 * `?agent=`) is only ever used as the DEFAULT selection when it is itself
 * a member of the eligible set — never trusted as a bypass, and never
 * skips showing the picker when there's genuinely more than one option.
 */
export async function fetchCapabilityAgentSelection(
  capability: string,
  preselectAgentId?: string | null,
): Promise<CapabilityAgentSelectionState> {
  const res = await fetch(`/api/capabilities/${encodeURIComponent(capability)}/agents`);
  if (!res.ok) return { status: "error" };
  const body = (await res.json()) as { items: EligibleCapabilityAgent[] };
  const agents = body.items ?? [];
  if (agents.length === 0) return { status: "none" };
  if (agents.length === 1) return { status: "auto", agent: agents[0] };
  const preselected = preselectAgentId && agents.some((a) => a.agentId === preselectAgentId)
    ? preselectAgentId
    : null;
  return { status: "choose", agents, selectedAgentId: preselected };
}
