import type { NextRequest } from "next/server";
import { enc, proxyRawToGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string }> }

// Add more bill files to an existing claim (one item per file).
export async function POST(request: NextRequest, context: Ctx) {
  const { id } = await context.params;
  return proxyRawToGateway(request, `/expense-claims/${enc(id)}/items`);
}
