import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, getSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";

async function logoutAction() {
  "use server";
  await clearSession();
  redirect("/login");
}

export default async function AppLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const token = await getSession();
  if (!token) {
    redirect("/login");
  }

  // Hit /me to discover the email/roles for the top nav. If the token is
  // expired we kick the user back to the login screen.
  const meRes = await gateway.me(token);
  if (!meRes.ok) {
    await clearSession();
    redirect("/login?error=session-expired");
  }
  const me = (await meRes.json()) as { email?: string | null };

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
              <Link href="/knowledge">Knowledge</Link>
            </Button>
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
