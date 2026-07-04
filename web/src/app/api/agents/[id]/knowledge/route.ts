import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function GET(_req: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const res = await gateway.listAgentKnowledge(token, id);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}

export async function POST(request: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const body = (await request.json()) as { noteId: string };
  const res = await gateway.attachNoteToAgent(token, id, body.noteId);
  return new NextResponse(null, { status: res.status });
}
