import { NextResponse } from "next/server";
import { getSession } from "@/lib/auth";

// Browser -> Gateway: expense-claim payment read model (blockers computed by the Gateway).
interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function GET(_request: Request, context: RouteContext) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await context.params;
  const res = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${encodeURIComponent(id)}/expense-claim/payment`,
    { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" },
  );
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}
