import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { getSession, namespacedThreadId } from "@/lib/auth";
import { normalizeMode } from "@/lib/chat-history";

interface AgentThreadHistory {
  thread_id: string;
  messages: { role: string; content: string }[];
  mode: string;
  agent_id?: string | null;
}

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string }> },
) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const { id } = await context.params;
  const fullId = namespacedThreadId(token, id) ?? id;
  const agentId = new URL(request.url).searchParams.get("agent_id") || undefined;

  const res = await agent.getThread(token, fullId, agentId);
  if (!res.ok) {
    return NextResponse.json(
      { thread_id: id, messages: [], mode: "chat", agent_id: null },
      { status: res.status },
    );
  }
  const payload = (await res.json()) as AgentThreadHistory;
  return NextResponse.json({
    thread_id: id,
    messages: payload.messages ?? [],
    mode: normalizeMode(payload.mode),
    agent_id: payload.agent_id ?? null,
  });
}

export async function DELETE(
  _request: Request,
  context: { params: Promise<{ id: string }> },
) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id } = await context.params;
  const fullId = namespacedThreadId(token, id) ?? id;

  const res = await agent.clearThread(token, fullId);
  return new NextResponse(null, { status: res.status === 204 ? 204 : res.status });
}
