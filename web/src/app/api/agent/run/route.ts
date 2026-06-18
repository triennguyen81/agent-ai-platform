import { NextResponse, type NextRequest } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

interface AgentRunBody {
  task?: string;
  skills?: string[];
  max_turns?: number;
}

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

  const res = await agent.runTask({
    task,
    user_token: token,
    skills: body.skills,
    max_turns: body.max_turns ?? 8,
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
