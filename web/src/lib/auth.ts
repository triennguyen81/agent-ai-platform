import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const COOKIE_NAME = "aiq.session";

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
}

export function decodeUserId(token: string): string | null {
  try {
    const parts = token.split(".");
    if (parts.length < 2) return null;
    const padded = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const json = Buffer.from(padded, "base64").toString("utf8");
    const claims = JSON.parse(json) as Record<string, unknown>;
    const sub = claims["sub"];
    return typeof sub === "string" ? sub : null;
  } catch {
    return null;
  }
}
