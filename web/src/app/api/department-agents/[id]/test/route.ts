import { NextResponse } from "next/server";
import { agent } from "@/lib/api";
import { getSession } from "@/lib/auth";

// ACL V2-C: runs the current draft (or, if none, published version) of an
// ADMIN's own Department Agent through the real runtime pipeline — mirrors
// api/system-agents/[id]/test exactly, except `test: true` tells the Agent
// service to ask the Gateway for GET /runtime/agents/{id}/manifest?mode=test
// instead of the normal production manifest. Authorization is entirely the
// Gateway's: that query only resolves for an ADMIN whose own department
// structurally owns this agent — anyone else gets a 403 straight from the
// runtime, same as the platform Test panel's existing super_admin-only gate.
export async function POST(request: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const body = (await request.json().catch(() => ({}))) as { prompt?: string };
  const prompt = (body.prompt ?? "").trim();
  if (!prompt) return NextResponse.json({ error: "prompt is required" }, { status: 400 });

  const res = await agent.chat(token, { prompt, agent_id: id, test: true });
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
