import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

// Document workflow review decision (require_review). Only the document's
// own department ADMIN is accepted — enforced by the Gateway
// (department_admin + department scope); this route only forwards the
// caller's own session token.
export async function POST(request: NextRequest, context: { params: Promise<{ id: string; taskId: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id, taskId } = await context.params;
  const body = (await request.json().catch(() => ({}))) as { decision?: string; note?: string };
  const res = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${id}/document-review/${taskId}/decision`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ decision: body.decision, note: body.note }),
      cache: "no-store",
    },
  );
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}
