import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

// Browser -> Gateway proxy for the Chứng từ THU / CHI settlement actions.
// The Gateway enforces department_admin + tenant/department scope; this
// route only forwards the session token and a whitelisted action name.
const ACTIONS = new Set(["post", "allocate", "unapply", "reverse", "discount-confirm"]);
// URL segment per action (discount confirmation lives under settlement/discount/confirm).
const PATHS: Record<string, string> = { "discount-confirm": "discount/confirm" };

interface RouteContext {
  params: Promise<{ id: string; action: string }>;
}

export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id, action } = await context.params;
  if (!ACTIONS.has(action)) {
    return NextResponse.json({ error: "unknown_action" }, { status: 404 });
  }
  const body = await request.text();
  const gatewayRes = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${encodeURIComponent(id)}/settlement/${PATHS[action] ?? action}`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body || "{}",
      cache: "no-store",
    },
  );
  const text = await gatewayRes.text();
  return new NextResponse(text || "{}", {
    status: gatewayRes.status,
    headers: { "Content-Type": "application/json" },
  });
}
