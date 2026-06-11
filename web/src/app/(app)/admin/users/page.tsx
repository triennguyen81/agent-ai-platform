import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, requireSession } from "@/lib/auth";
import { isClerkConfigured } from "@/lib/env";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface UserWithRoles {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
  createdAt: string;
  externalProvider: string | null;
  roles: string[];
}

async function inviteAction(formData: FormData): Promise<{ ok: boolean; message?: string }> {
  "use server";

  if (!isClerkConfigured) {
    return { ok: false, message: "Clerk is not configured." };
  }

  const email = String(formData.get("email") ?? "").trim();
  if (!email) {
    return { ok: false, message: "Email is required." };
  }

  // Use Clerk Backend SDK to send the invitation email. The user clicks the
  // link, signs up at Clerk, then hits /api/auth/sync which provisions a
  // user record in Gateway.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { clerkClient } = require("@clerk/nextjs/server") as typeof import("@clerk/nextjs/server");
  try {
    const client = await clerkClient();
    await client.invitations.createInvitation({
      emailAddress: email,
      redirectUrl: "/api/auth/sync?to=/chat",
    });
    revalidatePath("/admin/users");
    return { ok: true };
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : "Invite failed";
    return { ok: false, message };
  }
}

async function adminInviteForm(formData: FormData): Promise<void> {
  "use server";
  const result = await inviteAction(formData);
  if (!result.ok) {
    redirect(`/admin/users?error=${encodeURIComponent(result.message ?? "failed")}`);
  }
  redirect("/admin/users?ok=1");
}

function formatDate(s: string): string {
  try {
    return new Date(s).toLocaleString("vi-VN", {
      dateStyle: "medium",
      timeStyle: "short",
    });
  } catch {
    return s;
  }
}

function badge(role: string): string {
  switch (role) {
    case "admin":
      return "bg-[var(--color-primary)] text-[var(--color-primary-fg)]";
    case "reviewer":
      return "bg-[var(--color-accent)] text-[var(--color-fg)]";
    case "uploader":
      return "bg-[var(--color-muted)] text-[var(--color-fg)]";
    default:
      return "border text-[var(--color-muted-fg)]";
  }
}

export default async function AdminUsersPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.listUsers(token);

  if (res.status === 401) {
    await clearSession();
    redirect("/sign-in?error=session-expired");
  }
  if (res.status === 403) {
    redirect("/chat");
  }

  const body = res.ok
    ? ((await res.json()) as { items: UserWithRoles[]; total: number })
    : { items: [], total: 0 };

  const { ok, error } = await searchParams;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Người dùng</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Tổng {body.total} tài khoản · Admin có thể mời người dùng mới qua email.
          </p>
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Email</th>
                <th className="px-4 py-2 text-left">Tên</th>
                <th className="px-4 py-2 text-left">Vai trò</th>
                <th className="px-4 py-2 text-left">Nguồn</th>
                <th className="px-4 py-2 text-left">Tạo lúc</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                    Chưa có người dùng.
                  </td>
                </tr>
              ) : (
                body.items.map((u) => (
                  <tr key={u.id} className="border-t">
                    <td className="px-4 py-3">{u.email}</td>
                    <td className="px-4 py-3 text-[var(--color-muted-fg)]">
                      {u.displayName ?? "—"}
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap gap-1">
                        {u.roles.length === 0 ? (
                          <span className="text-xs text-[var(--color-muted-fg)]">no role</span>
                        ) : (
                          u.roles.map((r) => (
                            <span
                              key={r}
                              className={`rounded-full px-2 py-0.5 text-xs ${badge(r)}`}
                            >
                              {r}
                            </span>
                          ))
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                      {u.externalProvider ?? "email"}
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                      {formatDate(u.createdAt)}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">Mời người dùng mới</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Clerk sẽ gửi email với magic link. Người nhận click → tạo tài khoản → tự động
          được provision trong hệ thống.
        </p>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            Đã gửi lời mời.
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}

        <form action={adminInviteForm} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="invite-email">Email</Label>
            <Input id="invite-email" name="email" type="email" required placeholder="user@company.com" />
          </div>
          <Button type="submit" className="w-full" disabled={!isClerkConfigured}>
            Gửi lời mời
          </Button>
          {!isClerkConfigured && (
            <p className="text-xs text-[var(--color-muted-fg)]">
              Cần cấu hình <code>NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY</code> và{" "}
              <code>CLERK_SECRET_KEY</code> để dùng lời mời.
            </p>
          )}
        </form>
      </aside>
    </div>
  );
}
