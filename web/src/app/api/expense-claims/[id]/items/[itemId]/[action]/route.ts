import { NextResponse, type NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; itemId: string; action: string }> }

const ACTIONS = new Set(["exclude", "retry"]);

export async function POST(request: NextRequest, context: Ctx) {
  const { id, itemId, action } = await context.params;
  if (!ACTIONS.has(action)) return NextResponse.json({ error: "not_found" }, { status: 404 });
  return proxyToGateway(request, `/expense-claims/${enc(id)}/items/${enc(itemId)}/${action}`, "POST");
}
