import "server-only";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, requireSession } from "@/lib/auth";

export type LoginRole = "super_admin" | "admin" | "user";

export interface Viewer {
  token: string;
  sub: string;
  email: string;
  tenantId: string | null;
  roles: LoginRole[];
}

export async function requireViewer(allowed?: LoginRole[]): Promise<Viewer> {
  const token = await requireSession();
  const response = await gateway.me(token);
  if (!response.ok) {
    await clearSession();
    redirect("/login?error=session-expired");
  }
  const body = (await response.json()) as {
    sub?: string; email?: string; tenantId?: string | null; roles?: string[];
  };
  const roles = (body.roles ?? []).filter((role): role is LoginRole =>
    role === "super_admin" || role === "admin" || role === "user",
  );
  if (allowed && !allowed.some((role) => roles.includes(role))) redirect("/forbidden");
  return {
    token,
    sub: body.sub ?? "",
    email: body.email ?? "",
    tenantId: body.tenantId ?? null,
    roles,
  };
}
