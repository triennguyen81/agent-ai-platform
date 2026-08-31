import Link from "next/link";
import { redirect } from "next/navigation";
import { Search } from "lucide-react";
import { gateway } from "@/lib/api";
import { clearSession, requireSession } from "@/lib/auth";
import { Input } from "@/components/ui/input";

// System account directory — SUPER_ADMIN, read-only. Role/tenant changes
// happen exclusively through the authoritative flows:
//   ADMIN account:  Tenants → chọn tenant → Administrators
//   USER account:   (that tenant's) ADMIN → Users
// This page never grants roles and never assigns agent execution — both
// used to live here and were a confusing, easy-to-misuse duplicate of the
// real flows above.

interface UserDirectoryEntry {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
  createdAt: string;
  externalProvider: string | null;
  roles: string[];
  tenantCode: string | null;
  tenantName: string | null;
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
    case "super_admin":
      return "bg-red-600 text-white";
    case "admin":
      return "bg-[var(--color-primary)] text-[var(--color-primary-fg)]";
    case "user":
      return "bg-[var(--color-muted)] text-[var(--color-fg)]";
    default:
      return "border text-[var(--color-muted-fg)]"; // legacy roles (reviewer/uploader/viewer)
  }
}

const VISIBLE_ROLES = ["super_admin", "admin", "user"] as const;

function roleLabel(role: string): string {
  switch (role) {
    case "super_admin":
      return "Super admin";
    case "admin":
      return "Admin";
    case "user":
      return "User";
    default:
      return role;
  }
}

const ROLE_FILTERS = [
  ["all", "Tất cả"],
  ["super_admin", "Super admin"],
  ["admin", "Admin"],
  ["user", "User"],
  ["none", "Chưa có vai trò"],
] as const;

export default async function AdminUsersPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; role?: string }>;
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
    ? ((await res.json()) as { items: UserDirectoryEntry[]; total: number })
    : { items: [], total: 0 };

  const { q, role } = await searchParams;
  const query = (q ?? "").trim().toLowerCase();
  const roleFilter = role ?? "all";

  const filtered = body.items.filter((u) => {
    const matchesQuery =
      !query ||
      u.email.toLowerCase().includes(query) ||
      (u.displayName ?? "").toLowerCase().includes(query) ||
      (u.tenantName ?? "").toLowerCase().includes(query);
    const activeRoles = u.roles.filter((r) => (VISIBLE_ROLES as readonly string[]).includes(r));
    const matchesRole =
      roleFilter === "all" ||
      (roleFilter === "none" ? activeRoles.length === 0 : activeRoles.includes(roleFilter));
    return matchesQuery && matchesRole;
  });

  return (
    <div className="space-y-4">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Người dùng</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Danh mục tài khoản toàn hệ thống — chỉ xem. Tổng {body.total} tài khoản, đang hiển thị {filtered.length}.
          Để tạo administrator, vào{" "}
          <Link href="/admin/tenants" className="text-[var(--color-primary)] hover:underline">
            Tenants
          </Link>{" "}
          → chọn tenant → Administrators. Để tạo user, tenant ADMIN tạo trong Users của tenant đó.
        </p>

        <form className="flex flex-wrap items-center gap-3" action="/admin/users">
          <div className="relative w-full max-w-xs">
            <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-[var(--color-muted-fg)]" />
            <Input name="q" defaultValue={q ?? ""} placeholder="Tìm email, tên, tenant..." className="pl-8" />
          </div>
          {role && <input type="hidden" name="role" value={role} />}
          <button type="submit" className="sr-only">
            Tìm
          </button>
          <div className="flex flex-wrap gap-1 rounded-full border border-[var(--color-border)] p-1 text-xs">
            {ROLE_FILTERS.map(([key, label]) => (
              <Link
                key={key}
                href={`/admin/users?${new URLSearchParams({ ...(q ? { q } : {}), ...(key === "all" ? {} : { role: key }) }).toString()}`}
                className={`rounded-full px-3 py-1.5 transition ${
                  roleFilter === key
                    ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                    : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                }`}
              >
                {label}
              </Link>
            ))}
          </div>
        </form>
      </header>

      <div className="overflow-hidden rounded-[var(--radius)] border">
        <table className="w-full text-sm">
          <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
            <tr>
              <th className="px-4 py-2 text-left">Email</th>
              <th className="px-4 py-2 text-left">Tên</th>
              <th className="px-4 py-2 text-left">Vai trò</th>
              <th className="px-4 py-2 text-left">Tenant</th>
              <th className="px-4 py-2 text-left">Trạng thái</th>
              <th className="px-4 py-2 text-left">Nguồn</th>
            </tr>
          </thead>
          <tbody>
            {filtered.length === 0 ? (
              <tr>
                <td colSpan={6} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                  Không có tài khoản khớp bộ lọc.
                </td>
              </tr>
            ) : (
              filtered.map((u) => {
                const activeRoles = u.roles.filter((r) =>
                  (VISIBLE_ROLES as readonly string[]).includes(r),
                );
                return (
                  <tr key={u.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{u.email}</div>
                      <div className="text-[10px] text-[var(--color-muted-fg)]">
                        {formatDate(u.createdAt)}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-[var(--color-muted-fg)]">
                      {u.displayName ?? "—"}
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap gap-1">
                        {activeRoles.length === 0 ? (
                          <span className="text-xs text-[var(--color-muted-fg)]">chưa có vai trò</span>
                        ) : (
                          activeRoles.map((r) => (
                            <span
                              key={r}
                              className={`rounded-full px-2 py-0.5 text-xs ${badge(r)}`}
                            >
                              {roleLabel(r)}
                            </span>
                          ))
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-xs">
                      {u.tenantName ? (
                        <>
                          <div>{u.tenantName}</div>
                          <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{u.tenantCode}</div>
                        </>
                      ) : (
                        <span className="text-[var(--color-muted-fg)]">— (platform-level)</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs">
                      {u.isActive ? (
                        <span className="text-emerald-700">hoạt động</span>
                      ) : (
                        <span className="text-red-700">đã khoá</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                      {u.externalProvider ?? "email"}
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
