import type { NextRequest } from "next/server";
import { proxyRawToGateway } from "@/lib/gateway-proxy";

// ONE multipart upload (employee + trip + files[]) → ONE Expense Claim; the
// Gateway creates one item per file and enforces scope/validation.
export async function POST(request: NextRequest) {
  return proxyRawToGateway(request, "/expense-claims/");
}
