import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string }> }

// Contract payment-discount term (structured, revisioned). The Gateway
// enforces scope; only a department ADMIN may PUT, and only while the
// contract is still editable.
export async function GET(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyToGateway(request, `/contracts/${enc(id)}/payment-discount`, "GET");
}

export async function PUT(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyToGateway(request, `/contracts/${enc(id)}/payment-discount`, "PUT");
}
