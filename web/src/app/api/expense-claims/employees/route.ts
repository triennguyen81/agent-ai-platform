import type { NextRequest } from "next/server";
import { proxyToGateway } from "@/lib/gateway-proxy";

export async function GET(request: NextRequest) {
  return proxyToGateway(request, "/expense-claims/employees", "GET");
}
