import { NextResponse, type NextRequest } from "next/server";
import { agent } from "@/lib/api";
import { decodeUserId, getSession } from "@/lib/auth";
import { normalizeMode, type ChatMode } from "@/lib/chat-history";

interface AgentThreadSummary {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
  mode: string;
  agent_id?: string | null;
}

interface UiThreadSummary {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
  mode: ChatMode;
  agent_id: string | null;
}

export async function GET(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ items: [] }, { status: 401 });
  }
  const userId = decodeUserId(token);
  if (!userId) {
    return NextResponse.json({ items: [] }, { status: 401 });
  }

  const modeParam = request.nextUrl.searchParams.get("mode");
  const mode = modeParam === "agentic" ? "agentic" : modeParam === "chat" ? "chat" : undefined;
  const agentId = request.nextUrl.searchParams.get("agent_id") || undefined;

  const prefix = `${userId}:`;
  const res = await agent.listThreads(token, prefix, 50, mode, agentId);
  if (!res.ok) {
    return NextResponse.json({ items: [] }, { status: res.status });
  }

  const items = (await res.json()) as AgentThreadSummary[];
  const stripped: UiThreadSummary[] = items.map((s) => ({
    thread_id: s.thread_id.startsWith(prefix)
      ? s.thread_id.slice(prefix.length)
      : s.thread_id,
    title: s.title,
    message_count: s.message_count,
    updated_at: s.updated_at,
    mode: normalizeMode(s.mode),
    agent_id: s.agent_id ?? null,
  }));

  return NextResponse.json({ items: stripped });
}
