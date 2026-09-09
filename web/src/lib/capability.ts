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
 * never picks, knows, or sends an agent id.
 */
export async function executeCapability(
  capability: string,
  task: string,
  maxTurns = 4,
): Promise<string> {
  const res = await fetch("/api/agent/run", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ task, max_turns: maxTurns, capability }),
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
