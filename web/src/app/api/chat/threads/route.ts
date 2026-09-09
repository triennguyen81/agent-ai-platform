import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { decodeUserId, getSession } from "@/lib/auth";

interface AgentThreadSummary {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
}

interface UiThreadSummary {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
}

export async function GET() {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ items: [] }, { status: 401 });
  }
  const userId = decodeUserId(token);
  if (!userId) {
    return NextResponse.json({ items: [] }, { status: 401 });
  }

  const prefix = `${userId}:`;
  const res = await agent.listThreads(token, prefix, 50);
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
  }));

  return NextResponse.json({ items: stripped });
}
