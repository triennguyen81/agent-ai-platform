import { NextResponse, type NextRequest } from "next/server";
import { OWNERS, enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ owner: string; ownerId: string }> }

// Create a bank account for a customer/supplier (Gateway: department_admin).
export async function POST(request: NextRequest, context: Ctx) {
  const { owner, ownerId } = await context.params;
  if (!OWNERS.has(owner)) return NextResponse.json({ error: "not_found" }, { status: 404 });
  return proxyToGateway(request, `/${owner}/${enc(ownerId)}/bank-accounts`, "POST");
}
