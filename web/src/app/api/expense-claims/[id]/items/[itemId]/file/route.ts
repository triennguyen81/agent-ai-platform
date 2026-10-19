import { enc, proxyFileFromGateway } from "@/lib/gateway-proxy";

interface Ctx { params: Promise<{ id: string; itemId: string }> }

export async function GET(_request: Request, context: Ctx) {
  const { id, itemId } = await context.params;
  return proxyFileFromGateway(`/expense-claims/${enc(id)}/items/${enc(itemId)}/file`);
}
