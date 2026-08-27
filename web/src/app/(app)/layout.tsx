import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, getSession } from "@/lib/auth";
import { isClerkConfigured } from "@/lib/env";
import { NotificationBell } from "@/components/notification-bell";
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
  const isSuperAdmin = roles.includes("super_admin");
  const isTenantAdmin = roles.includes("admin");

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b border-[var(--color-border)] bg-[var(--color-bg)]/95 backdrop-blur">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-3">
          <Link href="/chat" className="font-serif text-xl tracking-tight">
            Cát Tường AI
          </Link>
          <div className="flex items-center gap-2">
            <AppNav
              email={me.email ?? "you"}
              isSuperAdmin={isSuperAdmin}
              isTenantAdmin={isTenantAdmin}
              signOutAction={logoutAction}
            />
            <NotificationBell />
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-6">{children}</main>
    </div>
  );
}
