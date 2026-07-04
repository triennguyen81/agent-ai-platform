import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function DELETE(_req: Request, ctx: { params: Promise<{ id: string; noteId: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id, noteId } = await ctx.params;
  const res = await gateway.detachNoteFromAgent(token, id, noteId);
  return new NextResponse(null, { status: res.status });
}
