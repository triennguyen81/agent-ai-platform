import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

// Browser -> Gateway proxy: ADMIN confirms or corrects a voucher's type.
// The Gateway enforces department_admin, department scope and "not posted".
interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id } = await context.params;
  const body = await request.text();
  const gatewayRes = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${encodeURIComponent(id)}/voucher-type`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body || "{}",
      cache: "no-store",
    },
  );
  const text = await gatewayRes.text();
  return new NextResponse(text || "{}", { status: gatewayRes.status, headers: { "Content-Type": "application/json" } });
}
