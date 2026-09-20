import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";
export async function POST(_: Request, { params }: { params: Promise<{ id: string }> }) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const { id } = await params;
  const r = await gateway.reactivateSupplier(token, id);
  return new NextResponse(await r.text(), { status: r.status, headers: { "Content-Type": r.headers.get("content-type") ?? "application/json" } });
}
