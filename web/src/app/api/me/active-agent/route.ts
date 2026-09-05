import { NextResponse, type NextRequest } from "next/server";
import { getSession, setActiveAgent, clearActiveAgent } from "@/lib/auth";
import { gateway } from "@/lib/api";

interface Body {
  agentId?: string;
}

interface MyAgent {
  agentId?: string;
  id?: string;
}

// Switch active agent — user chọn agent nào làm context mặc định.
// Verify agentId thuộc list assigned agents của user trước khi set (tránh
// user tự chọn agent không được assign).
export async function POST(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const body = (await request.json().catch(() => ({}))) as Body;
  const agentId = body.agentId?.trim() ?? "";

  if (!agentId) {
    await clearActiveAgent();
    return NextResponse.json({ activeAgentId: null });
  }

  // Verify agent thuộc user's assigned list.
  const meAgentsRes = await gateway.listMyAgents(token);
  if (!meAgentsRes.ok) {
    return NextResponse.json({ error: "cannot verify agent access" }, { status: 502 });
  }
  const list = (await meAgentsRes.json()) as MyAgent[] | { items?: MyAgent[] };
  const items = Array.isArray(list) ? list : (list.items ?? []);
  const found = items.some((a) => (a.agentId ?? a.id) === agentId);
  if (!found) {
    return NextResponse.json(
      { error: "agent_not_assigned", detail: "Agent không nằm trong danh sách được cấp cho bạn" },
      { status: 403 },
    );
  }

  await setActiveAgent(agentId);
  return NextResponse.json({ activeAgentId: agentId });
}
