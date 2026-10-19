import { NextResponse, type NextRequest } from "next/server";
import { OWNERS, enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ owner: string; ownerId: string; accountId: string; action: string }> }

// Deactivate / reactivate (soft — never deleted). Gateway: department_admin.
export async function POST(request: NextRequest, context: Ctx) {
  const { owner, ownerId, accountId, action } = await context.params;
  if (!OWNERS.has(owner) || (action !== "deactivate" && action !== "reactivate"))
    return NextResponse.json({ error: "not_found" }, { status: 404 });
  return proxyToGateway(request, `/${owner}/${enc(ownerId)}/bank-accounts/${enc(accountId)}/${action}`, "POST");
}
