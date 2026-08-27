import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";

export async function GET() {
  const token = await getSession(); if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const response = await gateway.listWorkflows(token); return new NextResponse(await response.text(), { status: response.status, headers: { "Content-Type": "application/json" } });
}
export async function POST(request: Request) {
  const token = await getSession(); if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const response = await gateway.createWorkflow(token, await request.json()); return new NextResponse(await response.text(), { status: response.status, headers: { "Content-Type": "application/json" } });
}
