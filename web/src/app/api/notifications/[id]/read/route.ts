import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function PATCH(
  _request: Request,
  context: { params: Promise<{ id: string }> },
) {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const { id } = await context.params;
  const res = await gateway.markNotificationRead(token, id);
  return new NextResponse(null, { status: res.status });
}
