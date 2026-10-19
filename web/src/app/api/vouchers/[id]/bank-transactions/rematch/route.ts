import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string }> }

// Re-run identification for lines ADMIN has not confirmed.
export async function POST(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyToGateway(request, `/vouchers/${enc(id)}/bank-transactions/rematch`, "POST");
}
