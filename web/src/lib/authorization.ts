import "server-only";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

export type LoginRole = "super_admin" | "tenant_owner" | "admin" | "user";

export interface Viewer {
  token: string;
  sub: string;
  email: string;
  tenantId: string | null;
  roles: LoginRole[];
  // D5 §13: /me already carries these (set by D2's TenantContextMiddleware)
  // — surfaced here so any page needing "own department identity" (ADMIN's
  // self-view) can read it off the viewer it already fetches, instead of a
  // duplicate API call. Null for tenant_owner/super_admin (departmentless).
  departmentId: string | null;
  departmentCode: string | null;
  departmentName: string | null;
}

export async function requireViewer(allowed?: LoginRole[]): Promise<Viewer> {
  const token = await requireSession();
  const response = await gateway.me(token);
  if (!response.ok) {
    redirect("/login?error=session-expired");
  }
  const body = (await response.json()) as {
    sub?: string; email?: string; tenantId?: string | null; roles?: string[];
    departmentId?: string | null; departmentCode?: string | null; departmentName?: string | null;
  };
  const roles = (body.roles ?? []).filter((role): role is LoginRole =>
    role === "super_admin" || role === "tenant_owner" || role === "admin" || role === "user",
  );
  if (allowed && !allowed.some((role) => roles.includes(role))) redirect("/forbidden");
  return {
    token,
    sub: body.sub ?? "",
    email: body.email ?? "",
    tenantId: body.tenantId ?? null,
    roles,
    departmentId: body.departmentId ?? null,
    departmentCode: body.departmentCode ?? null,
    departmentName: body.departmentName ?? null,
  };
}
