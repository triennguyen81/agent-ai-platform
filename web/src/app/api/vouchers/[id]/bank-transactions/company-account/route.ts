import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string }> }

// Link the statement's own account to an EXISTING company bank account (Gateway: department_admin).
export async function POST(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyToGateway(request, `/vouchers/${enc(id)}/bank-transactions/company-account`, "POST");
}
