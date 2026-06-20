import { NextResponse } from "next/server";
import { getSession } from "@/lib/auth";

interface ServiceCheck {
  name: string;
  url: string;
  ok: boolean;
  status: number;
  ms: number;
  detail?: string;
}

const GATEWAY_BASE = process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080";
const AGENT_BASE = process.env.AGENT_BASE_URL ?? "http://aiq-agent:8000";

async function ping(name: string, url: string, timeoutMs = 5000): Promise<ServiceCheck> {
  const started = Date.now();
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const res = await fetch(url, {
      method: "GET",
      cache: "no-store",
      signal: controller.signal,
    });
    clearTimeout(timer);
    return {
      name,
      url,
      ok: res.ok,
      status: res.status,
      ms: Date.now() - started,
    };
  } catch (err) {
    return {
      name,
      url,
      ok: false,
      status: 0,
      ms: Date.now() - started,
      detail: err instanceof Error ? err.message : "Network error",
    };
  }
}

export async function GET() {
  const token = await getSession();
  if (!token) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }

  // Hit /me to verify super_admin role.
  const meRes = await fetch(`${GATEWAY_BASE}/me`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  });
  if (!meRes.ok) {
    return NextResponse.json({ error: "session-expired" }, { status: 401 });
  }
  const me = (await meRes.json()) as { roles?: string[] };
  if (!(me.roles ?? []).includes("super_admin")) {
    return NextResponse.json({ error: "forbidden" }, { status: 403 });
  }

  const checks = await Promise.all([
    ping("Gateway · /healthz/live", `${GATEWAY_BASE}/healthz/live`),
    ping("Gateway · /healthz/ready", `${GATEWAY_BASE}/healthz/ready`),
    ping("Agent · /healthz/live", `${AGENT_BASE}/healthz/live`),
    ping("Agent · /healthz/ready", `${AGENT_BASE}/healthz/ready`),
    ping("Agent · /skills/list", `${AGENT_BASE}/skills/list`),
  ]);

  const okCount = checks.filter((c) => c.ok).length;
  const overall: "healthy" | "degraded" | "down" =
    okCount === checks.length
      ? "healthy"
      : okCount === 0
      ? "down"
      : "degraded";

  return NextResponse.json({
    overall,
    checked_at: new Date().toISOString(),
    checks,
  });
}
