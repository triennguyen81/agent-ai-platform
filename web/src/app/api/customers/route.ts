import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";
export async function POST(req: Request) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const body = await req.json();
  const r = await gateway.createCustomer(token, body);
  return new NextResponse(await r.text(), { status: r.status, headers: { "Content-Type": r.headers.get("content-type") ?? "application/json" } });
}
