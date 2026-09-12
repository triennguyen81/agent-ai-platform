import { NextResponse, type NextRequest } from "next/server";
import { GATEWAY_BASE } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Sprint 6' A3: proxy request /api/vouchers/export → gateway /vouchers/export
 * để attach JWT vào Authorization header. Gateway stream ZIP về, ta forward
 * y hệt kèm Content-Disposition để browser download đúng filename.
 *
 * Chỉ cần với user role=user (kế toán viên) vì cookie httpOnly không đọc
 * được từ href="/api/vouchers/export" trong <a> tag.
 */
export async function GET(request: NextRequest) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  const url = new URL(request.url);
  const qs = url.search; // includes leading '?' or empty

  const upstream = await fetch(`${GATEWAY_BASE}/vouchers/export${qs}`, {
    method: "GET",
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  });

  if (!upstream.ok) {
    const text = await upstream.text();
    return new NextResponse(text || "Export failed", { status: upstream.status });
  }

  const contentDisposition =
    upstream.headers.get("content-disposition") ??
    `attachment; filename="vouchers-export.zip"`;

  return new NextResponse(upstream.body, {
    status: 200,
    headers: {
      "Content-Type": "application/zip",
      "Content-Disposition": contentDisposition,
      "Cache-Control": "no-store",
    },
  });
}
