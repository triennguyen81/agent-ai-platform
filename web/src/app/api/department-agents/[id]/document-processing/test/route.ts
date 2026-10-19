import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

// Agent Builder TEST mode for document processing: the Gateway runs the
// current draft (else published) configuration against a sample through the
// tool-less extraction runtime and returns the result — no voucher, journal,
// AR/AP, settlement or payment is ever written.
export async function POST(request: Request, ctx: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await ctx.params;
  const body = (await request.json().catch(() => ({}))) as {
    rawText?: string;
    voucherId?: string;
    voucherTypeHint?: string;
  };
  if (!body.rawText?.trim() && !body.voucherId) {
    return NextResponse.json({ error: "sample_text_required" }, { status: 400 });
  }
  const res = await gateway.testDepartmentAgentDocumentProcessing(token, id, {
    rawText: body.rawText?.trim() || undefined,
    voucherId: body.voucherId || undefined,
    voucherTypeHint: body.voucherTypeHint || undefined,
  });
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
