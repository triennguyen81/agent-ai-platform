import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function GET() {
  const token = await getSession();
  if (!token) return NextResponse.json({ items: [] }, { status: 200 });
  const res = await gateway.listMyTenants(token);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
