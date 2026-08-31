import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

// Runs the current draft (or, if none, the published version) through the
// real runtime pipeline — same manifest -> executor path Phase 3C verified.
// Authorization is enforced entirely by the Gateway: /runtime/agents/{id}/manifest
// only resolves a super_admin test manifest for callers with the super_admin
// role, so a non-super-admin caller gets a 403 straight from the runtime.
export async function POST(request: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const body = (await request.json().catch(() => ({}))) as { prompt?: string };
  const prompt = (body.prompt ?? "").trim();
  if (!prompt) return NextResponse.json({ error: "prompt is required" }, { status: 400 });

  const res = await agent.chat(token, { prompt, agent_id: id });
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
