import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

// Browser -> Gateway: "Duyệt & chi tiền". Only the chosen payment source is
// forwarded; the Gateway enforces ADMIN, the claim's SoD, the tax gate, and
// computes the amount and journal itself.
interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function POST(request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await context.params;
  const body = (await request.json().catch(() => ({}))) as { paymentMethod?: unknown; companyBankAccountId?: unknown };
  const res = await fetch(
    `${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/vouchers/${encodeURIComponent(id)}/expense-claim/pay`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        paymentMethod: typeof body.paymentMethod === "string" ? body.paymentMethod : null,
        companyBankAccountId: typeof body.companyBankAccountId === "string" ? body.companyBankAccountId : null,
      }),
      cache: "no-store",
    },
  );
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}
