import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

interface RouteContext {
  params: Promise<{ id: string }>;
}

interface Body {
  decision?: string;
  note?: string;
}

// Proxy /api/approval-queue/{id}/review → gateway /approval-queue/{id}/review.
export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const { id } = await context.params;
  const body = (await request.json().catch(() => ({}))) as Body;

  const gatewayRes = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/approval-queue/${id}/review`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        decision: body.decision ?? "",
        note: body.note ?? null,
      }),
      cache: "no-store",
    },
  );

  const text = await gatewayRes.text();
  return new NextResponse(text, {
    status: gatewayRes.status,
    headers: { "Content-Type": "application/json" },
  });
}
