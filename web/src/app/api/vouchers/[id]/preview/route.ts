import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

/**
 * Proxy the voucher preview through Next.js so the browser doesn't
 * hit the gateway directly with the session token in a URL (which
 * would leak to `<img src>` referrers and browser history). Gateway
 * decides whether to redirect to a signed MinIO URL or stream the
 * bytes back — the browser sees this route as the ultimate origin.
 */
const GATEWAY_BASE = process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080";

interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function GET(_request: NextRequest, context: RouteContext) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id } = await context.params;
  const upstream = await fetch(`${GATEWAY_BASE}/vouchers/${id}/preview`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
    // Follow the redirect to MinIO signed URL server-side; the
    // browser stays talking to us.
    redirect: "follow",
  });

  if (!upstream.ok) {
    return new NextResponse(await upstream.text(), { status: upstream.status });
  }

  return new NextResponse(upstream.body, {
    status: 200,
    headers: {
      "Content-Type": upstream.headers.get("content-type") ?? "application/octet-stream",
      "Cache-Control": "private, max-age=60",
    },
  });
}
