import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, decodeTenantId, getSession } from "@/lib/auth";
import { isClerkConfigured } from "@/lib/env";
import { Button } from "@/components/ui/button";
import { NotificationBell } from "@/components/notification-bell";
import { TenantSwitcher } from "@/components/tenant-switcher";
import { AppNav } from "@/components/app-nav";

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
  const activeTenantId = decodeTenantId(token);

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b bg-[var(--color-bg)]/95 backdrop-blur">
        <div className="mx-auto flex max-w-7xl items-center gap-6 px-6 py-3">
          <Link href="/chat" className="font-serif text-xl shrink-0">
            Cát Tường AI
          </Link>
          <div className="flex flex-1 items-center justify-end gap-3">
            <AppNav roles={roles} />
            <div className="flex items-center gap-2 border-l pl-3">
              <TenantSwitcher activeTenantId={activeTenantId} />
              <NotificationBell />
              <span className="hidden text-xs text-[var(--color-muted-fg)] md:inline">
                {me.email ?? "you"}
              </span>
              <form action={logoutAction}>
                <Button variant="ghost" size="sm" type="submit">
                  Sign out
                </Button>
              </form>
            </div>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-7xl px-6 py-6">{children}</main>
    </div>
  );
}
