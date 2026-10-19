import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

// Browser -> Gateway proxy for tax verification actions. The Gateway enforces
// department_admin on confirm, the submitter rule and the evidence match;
// this route only forwards the session token and a whitelisted action.
const ACTIONS = new Set(["propose", "confirm", "reject"]);

interface RouteContext {
  params: Promise<{ id: string; action: string }>;
}

export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id, action } = await context.params;
  if (!ACTIONS.has(action)) return NextResponse.json({ error: "unknown_action" }, { status: 404 });
  const body = await request.text();
  const res = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${encodeURIComponent(id)}/tax-verification/${action}`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body || "{}",
      cache: "no-store",
    },
  );
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}
