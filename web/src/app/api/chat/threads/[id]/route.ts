import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { decodeUserId, getSession } from "@/lib/auth";

interface AgentThreadHistory {
  thread_id: string;
  messages: { role: string; content: string }[];
}

function buildKey(userId: string | null, raw: string): string {
  if (!userId) return raw;
  return raw.startsWith(`${userId}:`) ? raw : `${userId}:${raw}`;
}

export async function GET(
  _request: Request,
  context: { params: Promise<{ id: string }> },
) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const userId = decodeUserId(token);
  const { id } = await context.params;
  const fullId = buildKey(userId, id);

  const res = await agent.getThread(fullId);
  if (!res.ok) {
    return NextResponse.json({ thread_id: id, messages: [] }, { status: res.status });
  }
  const payload = (await res.json()) as AgentThreadHistory;
  return NextResponse.json({
    thread_id: id,
    messages: payload.messages ?? [],
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
  const userId = decodeUserId(token);
  const { id } = await context.params;
  const fullId = buildKey(userId, id);

  const res = await agent.clearThread(fullId);
  return new NextResponse(null, { status: res.status === 204 ? 204 : res.status });
}
