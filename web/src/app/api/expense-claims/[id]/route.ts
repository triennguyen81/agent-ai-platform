import type { NextRequest } from "next/server";
import { enc, proxyToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string }> }

export async function GET(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyToGateway(request, `/expense-claims/${enc(id)}`, "GET");
}
