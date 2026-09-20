import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";
export async function PATCH(req: Request, { params }: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await params;
  const body = await req.json();
  const r = await gateway.updateCustomer(token, id, body);
  return new NextResponse(await r.text(), { status: r.status, headers: { "Content-Type": r.headers.get("content-type") ?? "application/json" } });
}
