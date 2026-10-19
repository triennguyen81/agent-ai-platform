import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; txId: string }> }

// Statement line -> AR settlement context (customer, open AR, suggestion, blockers, posting).
export async function GET(request: NextRequest, context: Ctx) {
  const { id, txId } = await context.params;
  return proxyToGateway(request, `/vouchers/${enc(id)}/bank-transactions/${enc(txId)}/settlement`, "GET");
}
