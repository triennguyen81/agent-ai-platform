import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { Search, UserCheck, UserX } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { TenantRoleChangeButton } from "@/components/tenant-role-change-button";
import { ResetPasswordForm } from "@/components/reset-password-form";

interface TenantAccount {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
  role: "user" | "admin" | "tenant_owner";
  departmentId?: string | null;
  departmentName?: string | null;
}

interface Department { id: string; code: string; name: string }

function badge(role: TenantAccount["role"]): string {
  switch (role) {
    case "tenant_owner":
      return "bg-red-600 text-white";
    case "admin":
      return "bg-[var(--color-primary)] text-[var(--color-primary-fg)]";
    default:
      return "bg-[var(--color-muted)] text-[var(--color-fg)]";
  }
}

// ---- Server actions -------------------------------------------------
//
// Every action re-derives the viewer's real roles server-side via
// requireViewer() rather than trusting anything the form posts — a
// tampered "role=admin" field from a plain ADMIN's browser is silently
// downgraded here, on top of the gateway's own tenant_owner-only policy
// on the admin-targeting routes.

async function createAccountAction(formData: FormData): Promise<void> {
  "use server";
  const viewer = await requireViewer(["tenant_owner", "admin"]);
  const isOwner = viewer.roles.includes("tenant_owner");

  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  const displayName = String(formData.get("displayName") ?? "").trim() || undefined;
  const requestedRole = String(formData.get("role") ?? "user").trim().toLowerCase();
  const departmentId = String(formData.get("departmentId") ?? "").trim();
  // Never anything but "user" or "admin" reaches the gateway from here, and
  // only an actual TENANT_OWNER can ever select "admin" in the first place.
  const role = isOwner && requestedRole === "admin" ? "admin" : "user";

  if (!email || password.length < 8 || (isOwner && !departmentId)) {
    redirect("/admin/tenant-users?error=email-va-mat-khau-toi-thieu-8-ky-tu-la-bat-buoc");
  }
  const token = await requireSession();
  const res = role === "admin"
    ? await gateway.createOwnTenantAdmin(token, { email, password, displayName, departmentId })
    : await gateway.createTenantUser(token, {
        email, password, displayName, ...(isOwner ? { departmentId } : {}),
      });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-users?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect("/admin/tenant-users?ok=created");
}

async function setActiveAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "").trim();
  const targetRole = String(formData.get("targetRole") ?? "").trim();
  const action = String(formData.get("action") ?? "").trim(); // "enable" | "disable"
  if (!userId || (action !== "enable" && action !== "disable")) {
    redirect("/admin/tenant-users?error=yeu-cau-khong-hop-le");
  }
  if (targetRole === "admin") {
    // The gateway's /tenant/admins/* routes are tenant_owner-only — verify
    // here too so a plain ADMIN never even attempts the call.
    await requireViewer(["tenant_owner"]);
  }
  const token = await requireSession();
  const res = targetRole === "admin"
    ? await gateway.setOwnTenantAdminActive(token, userId, action === "enable")
    : action === "disable"
      ? await gateway.disableTenantUser(token, userId)
      : await gateway.enableTenantUser(token, userId);
  if (!res.ok) {
    redirect(`/admin/tenant-users?error=${encodeURIComponent(`${action}-that-bai`)}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect(`/admin/tenant-users?ok=${action}d`);
}

async function editAccountAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "").trim();
  const targetRole = String(formData.get("targetRole") ?? "").trim();
  const displayName = String(formData.get("displayName") ?? "").trim();
  if (!userId) redirect("/admin/tenant-users?error=yeu-cau-khong-hop-le");
  if (targetRole === "admin") {
    await requireViewer(["tenant_owner"]);
  }
  const token = await requireSession();
  const res = targetRole === "admin"
    ? await gateway.updateOwnTenantAdmin(token, userId, { displayName })
    : await gateway.updateTenantUser(token, userId, { displayName });
  if (!res.ok) redirect(`/admin/tenant-users?error=edit-that-bai-${res.status}`);
  revalidatePath("/admin/tenant-users");
  redirect("/admin/tenant-users?ok=updated");
}

async function changeRoleAction(userId: string, nextRole: "user" | "admin"): Promise<void> {
  "use server";
  // TENANT_OWNER-only action — requireViewer redirects to /forbidden for
  // anyone else, including a plain ADMIN who somehow invokes this action.
  await requireViewer(["tenant_owner"]);
  if (!userId || (nextRole !== "user" && nextRole !== "admin")) {
    redirect("/admin/tenant-users?error=yeu-cau-khong-hop-le");
  }
  const token = await requireSession();
  const res = await gateway.changeTenantUserRole(token, userId, nextRole);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-users?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect(`/admin/tenant-users?ok=role-changed`);
}

// D5 §9: only TENANT_OWNER may move an ADMIN/USER between departments —
// security-sensitive (moving a USER changes which agents/capabilities they
// can reach), so this re-derives the real role server-side exactly like
// changeRoleAction above, on top of the gateway's own tenant_owner-only
// policy on this route.
// Admin-facing reset-password action for the unified accounts table.
// targetRole picks the endpoint (and therefore the exact same server-side
// scope each already enforces): "admin" only reachable by TENANT_OWNER
// (gateway rejects anyone else), "user" reachable by ADMIN (own department)
// or TENANT_OWNER (own tenant). Never called for a tenant_owner row — that
// action is not rendered in the UI at all (see isReadOnlyOwner below).
async function resetPasswordAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "").trim();
  const targetRole = String(formData.get("targetRole") ?? "").trim();
  const newPassword = String(formData.get("newPassword") ?? "");
  const confirmPassword = String(formData.get("confirmPassword") ?? "");
  if (!userId || newPassword.length < 8) {
    redirect("/admin/tenant-users?error=mat-khau-moi-toi-thieu-8-ky-tu");
  }
  if (newPassword !== confirmPassword) {
    redirect("/admin/tenant-users?error=xac-nhan-mat-khau-khong-khop");
  }
  if (targetRole === "admin") {
    // The gateway's /tenant/admins/* routes are tenant_owner-only — verify
    // here too so a plain ADMIN never even attempts the call.
    await requireViewer(["tenant_owner"]);
  }
  const token = await requireSession();
  const res = targetRole === "admin"
    ? await gateway.resetOwnTenantAdminPassword(token, userId, { newPassword, confirmPassword })
    : await gateway.resetTenantUserPassword(token, userId, { newPassword, confirmPassword });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-users?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect("/admin/tenant-users?ok=password-reset");
}

async function moveDepartmentAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const userId = String(formData.get("userId") ?? "").trim();
  const departmentId = String(formData.get("departmentId") ?? "").trim();
  if (!userId || !departmentId) redirect("/admin/tenant-users?error=yeu-cau-khong-hop-le");
  const token = await requireSession();
  const res = await gateway.changeTenantUserDepartment(token, userId, departmentId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-users?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect("/admin/tenant-users?ok=department-moved");
}

export default async function TenantUsersPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string; q?: string; role?: string; dept?: string }>;
}) {
  const viewer = await requireViewer(["tenant_owner", "admin"]);
  const isOwner = viewer.roles.includes("tenant_owner");
  const { ok, error, q, role, dept } = await searchParams;

  // Plain ADMIN keeps exactly the pre-merge experience: role=user only, no
  // admin visibility, no role-change action, create form forced to "user".
  // Only TENANT_OWNER sees the unified USER+ADMIN screen — this task is
  // TENANT_OWNER-only and must not change what an ADMIN account can do.
  const [usersResponse, adminsResponse, departmentsResponse] = await Promise.all([
    gateway.listTenantUsers(viewer.token),
    isOwner ? gateway.listOwnTenantAdmins(viewer.token) : Promise.resolve(null),
    isOwner ? gateway.listTenantDepartments(viewer.token) : Promise.resolve(null),
  ]);
  const users = usersResponse.ok
    ? ((await usersResponse.json()) as { items: Omit<TenantAccount, "role">[] }).items
    : [];
  const admins = adminsResponse?.ok
    ? ((await adminsResponse.json()) as { items: Omit<TenantAccount, "role">[] }).items
    : [];
  const departments: Department[] = departmentsResponse?.ok
    ? ((await departmentsResponse.json()) as { items: Department[] }).items
    : [];

  const accounts: TenantAccount[] = [
    ...users.map((u) => ({ ...u, role: "user" as const })),
    ...admins.map((a) => ({ ...a, role: "admin" as const })),
  ];
  // The tenant owner's own account never comes back from either list above
  // (both are scoped to role=user / role=admin) — shown here as a
  // synthetic, always-read-only row so the screen reflects the full tenant
  // roster, per spec ("if the current tenant owner account is shown,
  // display it read-only").
  if (isOwner) {
    accounts.unshift({
      id: viewer.sub,
      email: viewer.email,
      displayName: null,
      isActive: true,
      role: "tenant_owner",
    });
  }

  const query = (q ?? "").trim().toLowerCase();
  const roleFilter = role ?? "all";
  const deptFilter = dept ?? "all";
  const filtered = accounts.filter((a) => {
    const matchesQuery =
      !query ||
      a.email.toLowerCase().includes(query) ||
      (a.displayName ?? "").toLowerCase().includes(query);
    const matchesRole = roleFilter === "all" || a.role === roleFilter;
    const matchesDept = deptFilter === "all" || a.departmentId === deptFilter;
    return matchesQuery && matchesRole && matchesDept;
  });

  const ROLE_FILTERS = [
    ["all", "Tất cả"],
    ["admin", "Admin"],
    ["user", "User"],
  ] as const;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header className="space-y-2">
          <h1 className="font-serif text-3xl">Người dùng</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Chỉ hiển thị tài khoản thuộc tenant đang đăng nhập. Tenant luôn được xác định từ phiên đăng nhập, không nhận từ client.
          </p>
          {!isOwner && viewer.departmentName && (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Bạn đang quản lý phòng ban:{" "}
              <span className="font-medium text-[var(--color-fg)]">{viewer.departmentName}</span>{" "}
              <span className="font-mono text-xs">({viewer.departmentCode})</span>
            </p>
          )}

          {isOwner && (
            <form className="flex flex-wrap items-center gap-3" action="/admin/tenant-users">
              <div className="relative w-full max-w-xs">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-[var(--color-muted-fg)]" />
                <Input name="q" defaultValue={q ?? ""} placeholder="Tìm email, tên..." className="pl-8" />
              </div>
              {role && <input type="hidden" name="role" value={role} />}
              <button type="submit" className="sr-only">Tìm</button>
              <div className="flex flex-wrap gap-1 rounded-full border border-[var(--color-border)] p-1 text-xs">
                {ROLE_FILTERS.map(([key, label]) => (
                  <Link
                    key={key}
                    href={`/admin/tenant-users?${new URLSearchParams({ ...(q ? { q } : {}), ...(dept && dept !== "all" ? { dept } : {}), ...(key === "all" ? {} : { role: key }) }).toString()}`}
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
              <div className="flex flex-wrap gap-1 rounded-full border border-[var(--color-border)] p-1 text-xs">
                <Link
                  href={`/admin/tenant-users?${new URLSearchParams({ ...(q ? { q } : {}), ...(role && role !== "all" ? { role } : {}) }).toString()}`}
                  className={`rounded-full px-3 py-1.5 transition ${
                    deptFilter === "all"
                      ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                      : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                  }`}
                >
                  Mọi phòng ban
                </Link>
                {departments.map((d) => (
                  <Link
                    key={d.id}
                    href={`/admin/tenant-users?${new URLSearchParams({ ...(q ? { q } : {}), ...(role && role !== "all" ? { role } : {}), dept: d.id }).toString()}`}
                    className={`rounded-full px-3 py-1.5 transition ${
                      deptFilter === d.id
                        ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                        : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                    }`}
                  >
                    {d.name}
                  </Link>
                ))}
              </div>
            </form>
          )}
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]">
              <tr>
                <th className="px-4 py-2 text-left">Email</th>
                <th className="px-4 py-2 text-left">Tên</th>
                {isOwner && <th className="px-4 py-2 text-left">Vai trò</th>}
                <th className="px-4 py-2 text-left">Phòng ban</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-right">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((account) => {
                const isReadOnlyOwner = account.role === "tenant_owner";
                return (
                  <tr key={account.id} className="border-t">
                    <td className="px-4 py-3">
                      <div className="font-medium">{account.email}</div>
                    </td>
                    <td className="px-4 py-3 text-[var(--color-muted-fg)]">
                      {account.displayName ?? "—"}
                    </td>
                    {isOwner && (
                      <td className="px-4 py-3">
                        <span className={`rounded-full px-2 py-0.5 text-xs ${badge(account.role)}`}>
                          {account.role.toUpperCase()}
                        </span>
                      </td>
                    )}
                    <td className="px-4 py-3 text-[var(--color-muted-fg)]">
                      {account.departmentName ?? "—"}
                    </td>
                    <td className="px-4 py-3">
                      {account.isActive ? (
                        <span className="inline-flex items-center gap-1 text-emerald-700"><UserCheck className="size-4" />hoạt động</span>
                      ) : (
                        <span className="inline-flex items-center gap-1 text-red-700"><UserX className="size-4" />đã khoá</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      {isReadOnlyOwner ? (
                        <span className="text-xs text-[var(--color-muted-fg)]">Chủ sở hữu tenant — chỉ xem</span>
                      ) : (
                        <div className="flex flex-wrap items-center justify-end gap-3">
                          {/*
                            ACL V2 final rule: TENANT_OWNER/ADMIN do not
                            manage Agent assignment at all — the "Agent
                            assignments" link here used to open the legacy
                            per-user tenant-agent entitlement screen
                            (removed). Agent assignment for a USER now only
                            ever happens on the owning Department Agent's own
                            page (/department/agents/[id] → tab Nhân sự), by
                            that department's ADMIN.
                          */}
                          <details className="text-left">
                            <summary className="cursor-pointer text-[var(--color-primary)]">Sửa</summary>
                            <form action={editAccountAction} className="mt-2 flex gap-2">
                              <input type="hidden" name="userId" value={account.id} />
                              <input type="hidden" name="targetRole" value={account.role} />
                              <Input name="displayName" defaultValue={account.displayName ?? ""} placeholder="Tên hiển thị" />
                              <Button type="submit" size="sm">Lưu</Button>
                            </form>
                          </details>
                          <form action={setActiveAction}>
                            <input type="hidden" name="userId" value={account.id} />
                            <input type="hidden" name="targetRole" value={account.role} />
                            <input type="hidden" name="action" value={account.isActive ? "disable" : "enable"} />
                            <Button type="submit" size="sm" variant={account.isActive ? "ghost" : "outline"}>
                              {account.isActive ? "Khoá" : "Mở lại"}
                            </Button>
                          </form>
                          <details className="text-left">
                            <summary className="cursor-pointer text-[var(--color-primary)]">
                              Đặt lại mật khẩu
                            </summary>
                            <ResetPasswordForm
                              userId={account.id}
                              targetRole={account.role}
                              email={account.email}
                              action={resetPasswordAction}
                            />
                          </details>
                          {isOwner && account.role !== "tenant_owner" && (
                            <details className="text-left">
                              <summary className="cursor-pointer text-[var(--color-primary)]">
                                Đổi phòng ban
                              </summary>
                              <form action={moveDepartmentAction} className="mt-2 flex gap-2">
                                <input type="hidden" name="userId" value={account.id} />
                                <select
                                  name="departmentId"
                                  required
                                  defaultValue={account.departmentId ?? ""}
                                  className="h-9 rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 text-sm"
                                >
                                  {departments.map((d) => (
                                    <option key={d.id} value={d.id}>{d.name}</option>
                                  ))}
                                </select>
                                <Button type="submit" size="sm">Chuyển</Button>
                              </form>
                            </details>
                          )}
                          {isOwner && account.role !== "tenant_owner" && (
                            <TenantRoleChangeButton
                              userId={account.id}
                              nextRole={account.role === "admin" ? "user" : "admin"}
                              label={account.role === "admin" ? "Demote to User" : "Promote to Admin"}
                              confirmMessage={
                                account.role === "admin"
                                  ? `Chuyển ${account.email} về USER?`
                                  : `Chuyển ${account.email} thành ADMIN?`
                              }
                              action={changeRoleAction}
                            />
                          )}
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
              {filtered.length === 0 && (
                <tr>
                  <td colSpan={isOwner ? 6 : 5} className="p-8 text-center text-[var(--color-muted-fg)]">
                    Không có tài khoản khớp bộ lọc.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">Tạo tài khoản</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          {isOwner
            ? "Luôn được tạo trong tenant của bạn. Không thể tạo TENANT_OWNER hoặc SUPER_ADMIN."
            : "Luôn được tạo trong tenant của bạn với vai trò \"user\". Không thể tạo ADMIN, TENANT_OWNER hoặc SUPER_ADMIN."}
        </p>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            {ok === "created" ? "Đã tạo tài khoản."
              : ok === "updated" ? "Đã cập nhật tài khoản."
              : ok === "disabled" ? "Đã khoá tài khoản."
              : ok === "role-changed" ? "Đã đổi vai trò."
              : ok === "department-moved" ? "Đã chuyển phòng ban."
              : ok === "password-reset" ? "Đã đặt lại mật khẩu. Người dùng cần đổi mật khẩu ở lần đăng nhập tiếp theo."
              : "Đã mở lại tài khoản."}
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <form action={createAccountAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="u-email">Email</Label>
            <Input id="u-email" name="email" type="email" required placeholder="user@company.com" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="u-password">Mật khẩu</Label>
            <Input id="u-password" name="password" type="password" required minLength={8} placeholder="Tối thiểu 8 ký tự" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="u-name">Tên hiển thị (không bắt buộc)</Label>
            <Input id="u-name" name="displayName" placeholder="Nguyễn Văn A" />
          </div>
          {isOwner && (
            <div className="space-y-1.5">
              <Label htmlFor="u-department">Phòng ban</Label>
              <select
                id="u-department"
                name="departmentId"
                required
                className="h-10 w-full rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-3 text-sm"
              >
                <option value="">Chọn phòng ban</option>
                {departments.map((department) => (
                  <option key={department.id} value={department.id}>{department.name}</option>
                ))}
              </select>
            </div>
          )}
          {isOwner && (
            <div className="space-y-1.5">
              <Label htmlFor="u-role">Vai trò</Label>
              <select
                id="u-role"
                name="role"
                defaultValue="user"
                className="h-10 w-full rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-3 text-sm focus:border-[var(--color-primary)] focus:outline-none"
              >
                <option value="user">User</option>
                <option value="admin">Admin</option>
              </select>
            </div>
          )}
          <Button type="submit" className="w-full">
            {isOwner ? "Tạo tài khoản" : "Tạo người dùng"}
          </Button>
        </form>
      </aside>
    </div>
  );
}
