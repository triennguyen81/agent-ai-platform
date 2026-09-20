import { NextRequest, NextResponse } from "next/server";
import { getSession } from "@/lib/auth";
import { GATEWAY_BASE } from "@/lib/api";

async function proxy(request: NextRequest, context: { params: Promise<{ path?: string[] }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { path = [] } = await context.params;
  if (path.some((part) => !/^[a-zA-Z0-9-]+$/.test(part))) return new NextResponse(null, { status: 400 });
  const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
  const contentType = request.headers.get("content-type");
  if (contentType) headers["Content-Type"] = contentType;
  const body = request.method === "GET" ? undefined : await request.arrayBuffer();
  if (body && body.byteLength > 11 * 1024 * 1024) return new NextResponse(null, { status: 413 });
  const response = await fetch(`${GATEWAY_BASE}/department/knowledge-bases/${path.join("/")}`, {
    method: request.method, headers, body, cache: "no-store",
  });
  return new NextResponse(response.status === 204 ? null : await response.text(), {
    status: response.status, headers: { "Content-Type": response.headers.get("content-type") ?? "application/json" },
  });
}
export { proxy as GET, proxy as POST, proxy as PATCH, proxy as PUT, proxy as DELETE };
