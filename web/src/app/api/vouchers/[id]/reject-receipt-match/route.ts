import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function POST(_request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id: voucherId } = await context.params;
  const gatewayRes = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${voucherId}/reject-receipt-match`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: "{}",
      cache: "no-store",
    },
  );
  const text = await gatewayRes.text();
  return new NextResponse(text, {
    status: gatewayRes.status,
    headers: { "Content-Type": "application/json" },
  });
}
