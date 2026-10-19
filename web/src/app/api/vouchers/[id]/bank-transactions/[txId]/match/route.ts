import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; txId: string }> }

// ADMIN confirms the Customer/Supplier for a statement line (optionally saves its bank account).
export async function POST(request: NextRequest, context: Ctx) {
  const { id, txId } = await context.params;
  return proxyToGateway(request, `/vouchers/${enc(id)}/bank-transactions/${enc(txId)}/match`, "POST");
}
