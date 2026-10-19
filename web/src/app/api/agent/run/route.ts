import { NextResponse, type NextRequest } from "next/server";
import { agent, gateway } from "@/lib/api";
import { getSession, getActiveAgent, namespacedThreadId } from "@/lib/auth";

interface AgentRunBody {
  task?: string;
  skills?: string[];
  max_turns?: number;
  agent_id?: string;
  capability?: string;
  thread_id?: string;
}

interface MyAgent {
  id?: string;
  agentId?: string;
  slug?: string;
}

// Sau merge colleague's multi-tenant refactor, agent runtime enforce agent_id
// khi caller là user-only role (theo agent/src/aiq_agent/api/routes.py:169).
// Auto-fallback: nếu client không truyền agent_id, lookup user's first
// assigned agent (từ /me/agents endpoint). Giữ backward-compat cho các page
// cũ (tax-calc, journal, invoice) chỉ pass task + skills.
export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const body = (await request.json()) as AgentRunBody;
  const task = String(body?.task ?? "").trim();
  if (!task) {
    return NextResponse.json({ error: "task is required" }, { status: 400 });
  }

  // Priority: request body agent_id > active_agent cookie > first assigned agent.
  // Active agent cookie set qua /api/me/active-agent — user chọn "project"
  // hiện tại trong SideNav. Fallback lookup /me/agents nếu chưa set cookie.
  let agentId = body.agent_id ?? (await getActiveAgent()) ?? undefined;
  if (!agentId) {
    try {
      const meAgentsRes = await gateway.listMyAgents(token);
      if (meAgentsRes.ok) {
        const list = (await meAgentsRes.json()) as MyAgent[] | { items?: MyAgent[] };
        const items = Array.isArray(list) ? list : (list.items ?? []);
        const first = items[0];
        agentId = first?.id ?? first?.agentId;
      }
    } catch {
      // Silent fallback — agent runtime sẽ trả lỗi 403 rõ ràng nếu vẫn thiếu.
    }
  }

  if (!agentId) {
    return NextResponse.json(
      {
        error: "no_agent_assigned",
        detail:
          "Tài khoản chưa được assign trợ lý nào. Liên hệ admin để cấp quyền, hoặc tenant_owner qua /admin/tenant-users assign.",
      },
      { status: 400 },
    );
  }

  const res = await agent.runTask(token, {
    task,
    // A capability request is resolved by the Gateway (agent_id and
    // capability are mutually exclusive). Otherwise send the agent resolved
    // above — previously body.agent_id was forwarded, so the cookie/first-
    // assigned fallback was computed but dropped and the request reached the
    // ungoverned no-agent runtime (token audit Phase 5).
    agent_id: body.capability ? body.agent_id : agentId,
    capability: body.capability,
    skills: body.skills,
    max_turns: body.max_turns ?? 8,
    thread_id: namespacedThreadId(token, body.thread_id),
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
