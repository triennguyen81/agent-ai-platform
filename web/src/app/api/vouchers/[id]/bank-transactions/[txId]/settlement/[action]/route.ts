import { NextResponse, type NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; txId: string; action: string }> }

// Canonical cash settlement actions for a statement line (Gateway: department_admin).
const ACTIONS = new Set(["post", "allocate", "unapply", "reverse", "reconcile", "discount-confirm"]);
const PATHS: Record<string, string> = { "discount-confirm": "discount/confirm" };

export async function POST(request: NextRequest, context: Ctx) {
  const { id, txId, action } = await context.params;
  if (!ACTIONS.has(action)) return NextResponse.json({ error: "not_found" }, { status: 404 });
  return proxyToGateway(request, `/vouchers/${enc(id)}/bank-transactions/${enc(txId)}/settlement/${PATHS[action] ?? action}`, "POST");
}
