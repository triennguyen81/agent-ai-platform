import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, decodeTenantId, getSession } from "@/lib/auth";
import { isClerkConfigured } from "@/lib/env";
import { Button } from "@/components/ui/button";
import { NotificationBell } from "@/components/notification-bell";
import { TenantSwitcher } from "@/components/tenant-switcher";

interface MeResponse {
  sub?: string | null;
  email?: string | null;
  roles?: string[];
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
  // KB contributors: admin OR super_admin.
  const canEditKnowledge = isSuperAdmin || roles.includes("admin");
  const activeTenantId = decodeTenantId(token);

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b bg-[var(--color-bg)]/95 backdrop-blur">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-3">
          <Link href="/chat" className="font-serif text-xl">
            Cát Tường AI
          </Link>
          <nav className="flex items-center gap-1">
            <Button variant="ghost" size="sm" asChild>
              <Link href="/chat">Chat</Link>
            </Button>
            <Button variant="ghost" size="sm" asChild>
              <Link href="/my-agents">Trợ lý</Link>
            </Button>
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/knowledge">Knowledge</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/accounting">Kế toán</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/contracts">Hợp đồng</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/vouchers">Chứng từ</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/customers">Khách hàng</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/admin/agents">Agents</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/admin/review-queue">Reviews</Link>
              </Button>
            )}
            {canEditKnowledge && (
              <Button variant="ghost" size="sm" asChild>
                <Link href="/admin/hitl-queue">HITL</Link>
              </Button>
            )}
            {isSuperAdmin && (
              <>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/tenants">Tenants</Link>
                </Button>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/tools">Tools</Link>
                </Button>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/users">Admin</Link>
                </Button>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/skills">Skills</Link>
                </Button>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/audit">Audit</Link>
                </Button>
                <Button variant="ghost" size="sm" asChild>
                  <Link href="/admin/health">Health</Link>
                </Button>
              </>
            )}
            <TenantSwitcher activeTenantId={activeTenantId} />
            <NotificationBell />
            <span className="ml-3 text-sm text-[var(--color-muted-fg)]">
              {me.email ?? "you"}
            </span>
            <form action={logoutAction}>
              <Button variant="ghost" size="sm" type="submit">
                Sign out
              </Button>
            </form>
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-6">{children}</main>
    </div>
  );
}
