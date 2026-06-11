import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function POST(request: Request) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const body = await request.json();
  if (typeof body?.prompt !== "string" || !body.prompt.trim()) {
    return NextResponse.json({ error: "missing prompt" }, { status: 400 });
  }

  const res = await agent.chat({
    prompt: body.prompt,
    thread_id: body.thread_id,
    temperature: body.temperature ?? 0.2,
  });

  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
