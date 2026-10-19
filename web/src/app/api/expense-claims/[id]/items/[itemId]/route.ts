import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; itemId: string }> }

// ADMIN confirmation/correction of one item (Gateway: department_admin).
export async function PATCH(request: NextRequest, context: Ctx) {
  const { id, itemId } = await context.params;
  return proxyToGateway(request, `/expense-claims/${enc(id)}/items/${enc(itemId)}`, "PATCH");
}
