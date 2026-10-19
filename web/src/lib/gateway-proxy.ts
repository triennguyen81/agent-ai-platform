import { NextResponse, type NextRequest } from "next/server";
import { getSession } from "@/lib/auth";

/** Forward a browser mutation to the Gateway with the session token. The Gateway enforces ACL and scope. */
export async function proxyToGateway(request: NextRequest, path: string, method: "GET" | "POST" | "PATCH" | "PUT") {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const body = method === "GET" ? undefined : (await request.text()) || "{}";
  const res = await fetch(`${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body,
    cache: "no-store",
  });
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}

export const OWNERS = new Set(["customers", "suppliers"]);
export const enc = encodeURIComponent;

/** Forward a multipart (or other raw) body as-is — the Gateway parses and validates the files. */
export async function proxyRawToGateway(request: NextRequest, path: string) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const res = await fetch(`${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}${path}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": request.headers.get("content-type") ?? "application/octet-stream" },
    body: await request.arrayBuffer(),
    cache: "no-store",
  });
  const text = await res.text();
  return new NextResponse(text || "{}", { status: res.status, headers: { "Content-Type": "application/json" } });
}

/** Stream a Gateway file response (content type + disposition preserved). */
export async function proxyFileFromGateway(path: string) {
  const token = await getSession();
  if (!token) return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  const res = await fetch(`${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  });
  const headers = new Headers();
  for (const h of ["content-type", "content-disposition", "content-length"]) {
    const value = res.headers.get(h);
    if (value) headers.set(h, value);
  }
  return new NextResponse(res.body, { status: res.status, headers });
}
