import { NextResponse, type NextRequest } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

// Generic "Xử lý bằng trợ lý" selector data proxy — reused by every
// capability-routed business page (Contract first; Voucher/Invoice/
// Customer/Supplier/Expense follow the same pattern). Never itself an
// authorization decision: the id the page later submits still goes through
// the Gateway's own preferredAgentId re-validation inside /agent/run.
export async function GET(_request: NextRequest, { params }: { params: Promise<{ code: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { code } = await params;
  const res = await gateway.listEligibleCapabilityAgents(token, code);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
