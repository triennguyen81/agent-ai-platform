import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

// Agent Builder → Document Processing: replaces the full document-type
// configuration of ONE draft version. Ownership (ADMIN of the owning
// department), draft-only editing and validation are all enforced by the
// Gateway — this route only forwards the caller's own session token.
export async function PUT(request: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const body = (await request.json().catch(() => ({}))) as { versionId?: string; types?: unknown[] };
  if (!body.versionId || !Array.isArray(body.types)) {
    return NextResponse.json({ error: "versionId and types are required" }, { status: 400 });
  }
  const res = await gateway.replaceDepartmentAgentDocumentProcessing(token, id, body.versionId, body.types);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
