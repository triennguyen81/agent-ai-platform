import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

/**
 * Simulator endpoint: web UI can post here to manually create a pending
 * HITL action (used for demoing the queue without wiring the agent runtime).
 */
export async function POST(request: Request) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const body = (await request.json()) as Record<string, unknown>;
  const res = await gateway.createHitlAction(token, body);
  const text = await res.text();
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": res.headers.get("content-type") ?? "application/json" },
  });
}
