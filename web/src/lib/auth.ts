import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const COOKIE_NAME = "aiq.session";
const ACTIVE_AGENT_COOKIE = "aiq.active_agent";

export async function setSession(token: string, expiresAt: Date): Promise<void> {
  const jar = await cookies();
  jar.set(COOKIE_NAME, token, {
    httpOnly: true,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax",
    expires: expiresAt,
    path: "/",
  });
}

export async function getSession(): Promise<string | null> {
  const jar = await cookies();
  return jar.get(COOKIE_NAME)?.value ?? null;
}

export async function requireSession(): Promise<string> {
  const token = await getSession();
  if (!token) {
    redirect("/login");
  }
  return token;
}

export async function clearSession(): Promise<void> {
  const jar = await cookies();
  jar.delete(COOKIE_NAME);
  jar.delete(ACTIVE_AGENT_COOKIE);
}

// Active agent — "project" hiện đang làm việc. User có thể có nhiều agent,
// mỗi agent là 1 specialist cho 1 lĩnh vực (kế toán, HR, legal, v.v.).
// Cookie 30 ngày, không httpOnly (client cần đọc để hiển thị active state).
export async function setActiveAgent(agentId: string): Promise<void> {
  const jar = await cookies();
  const expires = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000);
  jar.set(ACTIVE_AGENT_COOKIE, agentId, {
    httpOnly: false,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax",
    expires,
    path: "/",
  });
}

export async function getActiveAgent(): Promise<string | null> {
  const jar = await cookies();
  return jar.get(ACTIVE_AGENT_COOKIE)?.value ?? null;
}

export async function clearActiveAgent(): Promise<void> {
  const jar = await cookies();
  jar.delete(ACTIVE_AGENT_COOKIE);
}

export function decodeUserId(token: string): string | null {
  return decodeClaim(token, "sub");
}

export function decodeTenantId(token: string): string | null {
  return decodeClaim(token, "tenant_id");
}

function decodeClaim(token: string, claim: string): string | null {
  try {
    const parts = token.split(".");
    if (parts.length < 2) return null;
    const padded = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const json = Buffer.from(padded, "base64").toString("utf8");
    const claims = JSON.parse(json) as Record<string, unknown>;
    const v = claims[claim];
    return typeof v === "string" ? v : null;
  } catch {
    return null;
  }
}
