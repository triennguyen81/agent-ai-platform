import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession, setSession } from "@/lib/auth";

/**
 * Switch the user's active tenant by asking the gateway to re-issue a JWT
 * with the new tenant_id claim, then store it in the session cookie.
 */
export async function POST(request: Request) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const body = (await request.json()) as { tenantId: string };
  const res = await gateway.switchActiveTenant(token, body.tenantId);
  if (!res.ok) {
    return new NextResponse(await res.text(), { status: res.status });
  }
  const payload = (await res.json()) as { token: string; expiresAt: string; tenantId: string };
  await setSession(payload.token, new Date(payload.expiresAt));
  return NextResponse.json({ tenantId: payload.tenantId });
}
