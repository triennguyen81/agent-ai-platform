import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, getSession, getActiveAgent } from "@/lib/auth";
import { isClerkConfigured } from "@/lib/env";
import type { LoginRole } from "@/lib/authorization";
import { fetchMyCapabilityCodes } from "@/lib/my-capabilities";
import { NotificationBell } from "@/components/notification-bell";
import { SideNav, type SideNavAgent } from "@/components/side-nav";

interface MeResponse {
  sub?: string | null;
  email?: string | null;
  roles?: string[];
  mustChangePassword?: boolean;
}

interface MyAgent {
  agentId: string;
  agentCode: string;
  agentName: string;
  lifecycleState: string;
  agentDisabled: boolean;
}

async function logoutAction() {
  "use server";
  await clearSession();
  // When Clerk is on, we'd also want to call signOut() but the user can
  // click Clerk's own UserButton for that. Cookie clear is sufficient to
  // kick the user out of our app.
  redirect(isClerkConfigured ? "/sign-in" : "/login");
}

export default async function AppLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const token = await getSession();
  if (!token) {
    redirect(isClerkConfigured ? "/sign-in" : "/login");
  }

  // Hit /me to discover the email/roles for the top nav. If the token is
  // expired we kick the user back to the login screen.
  const meRes = await gateway.me(token);
  if (!meRes.ok) {
    await clearSession();
    redirect((isClerkConfigured ? "/sign-in" : "/login") + "?error=session-expired");
  }
  const me = (await meRes.json()) as MeResponse;
  const roles = me.roles ?? [];
  const isSuperAdmin = roles.includes("super_admin");
  const isTenantOwner = roles.includes("tenant_owner");
  const isTenantAdmin = roles.includes("admin");

  // Nghiệp vụ sidebar section is capability-driven (nav-model.ts
  // buildNghiepVuItems) — only pages backed by an actually-assigned
  // capability are shown, never a role-only guess.
  const loginRoles = roles.filter((role): role is LoginRole =>
    role === "super_admin" || role === "tenant_owner" || role === "admin" || role === "user",
  );
  const myCapabilityCodes = await fetchMyCapabilityCodes(token, loginRoles);
  const capabilityCodes = myCapabilityCodes ? [...myCapabilityCodes] : [];

  // Fetch user's assigned agents for sidebar "Trợ lý của bạn" section.
  // Fail-soft: nếu lỗi, sidebar vẫn render nhưng section agents trống.
  let agents: SideNavAgent[] = [];
  try {
    const agentsRes = await gateway.listMyAgents(token);
    if (agentsRes.ok) {
      const body = (await agentsRes.json()) as MyAgent[] | { items?: MyAgent[] };
      const items = Array.isArray(body) ? body : (body.items ?? []);
      agents = items.map((a) => ({
        agentId: a.agentId,
        agentName: a.agentName,
        agentCode: a.agentCode,
        lifecycleState: a.lifecycleState,
        agentDisabled: a.agentDisabled,
      }));
    }
  } catch {
    // silent fallback
  }
  const activeAgentId = await getActiveAgent();

  return (
    <div className="flex min-h-dvh">
      <SideNav
        email={me.email ?? "you"}
        isSuperAdmin={isSuperAdmin}
        isTenantOwner={isTenantOwner}
        isTenantAdmin={isTenantAdmin}
        capabilityCodes={capabilityCodes}
        agents={agents}
        activeAgentId={activeAgentId}
        signOutAction={logoutAction}
      />
      <div className="flex min-h-dvh flex-1 flex-col">
        {/* Header: mobile has hamburger space, desktop just NotificationBell */}
        <header className="sticky top-0 z-10 flex items-center justify-end gap-2 border-b border-[var(--color-border)] bg-[var(--color-bg)]/95 pl-14 pr-6 py-2 backdrop-blur md:pl-6">
          {!isSuperAdmin && <NotificationBell />}
        </header>
        {me.mustChangePassword && (
          <Link
            href="/profile"
            className="block bg-amber-50 px-6 py-2 text-center text-sm text-amber-800 hover:bg-amber-100"
          >
            Mật khẩu của bạn vừa được đặt lại — bấm để đổi sang mật khẩu mới của riêng bạn.
          </Link>
        )}
        <main className="flex-1 px-6 py-6">
          <div className="mx-auto max-w-6xl">{children}</div>
        </main>
      </div>
    </div>
  );
}
