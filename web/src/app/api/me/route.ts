import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

// Thin proxy so client components (e.g. Accounting → Hoá đơn) can check
// the current user's roles without a Server Component wrapping them —
// mirrors what vouchers/[id]/page.tsx already does server-side via
// gateway.me(token) for its own isAdmin gate.
export async function GET() {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const res = await gateway.me(token);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
