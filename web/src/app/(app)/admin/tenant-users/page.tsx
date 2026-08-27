import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { UserCheck, UserX } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface TenantUser { id: string; email: string; displayName: string | null; isActive: boolean; roles: string[] }

async function createUserAction(formData: FormData): Promise<void> {
  "use server";
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  const displayName = String(formData.get("displayName") ?? "").trim() || undefined;
  if (!email || password.length < 8) {
    redirect("/admin/tenant-users?error=email-va-mat-khau-toi-thieu-8-ky-tu-la-bat-buoc");
  }
  const token = await requireSession();
  const res = await gateway.createTenantUser(token, { email, password, displayName });
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
  const action = String(formData.get("action") ?? "").trim(); // "enable" | "disable"
  if (!userId || (action !== "enable" && action !== "disable")) {
    redirect("/admin/tenant-users?error=yeu-cau-khong-hop-le");
  }
  const token = await requireSession();
  const res = action === "disable"
    ? await gateway.disableTenantUser(token, userId)
    : await gateway.enableTenantUser(token, userId);
  if (!res.ok) {
    redirect(`/admin/tenant-users?error=${encodeURIComponent(`${action}-that-bai`)}`);
  }
  revalidatePath("/admin/tenant-users");
  redirect(`/admin/tenant-users?ok=${action}d`);
}

export default async function TenantUsersPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const viewer = await requireViewer(["admin"]);
  const response = await gateway.listTenantUsers(viewer.token);
  const users: TenantUser[] = response.ok ? ((await response.json()) as { items: TenantUser[] }).items : [];
  const { ok, error } = await searchParams;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Người dùng tenant</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Chỉ hiển thị người dùng thuộc tenant đang đăng nhập. Tenant luôn được xác định từ phiên đăng nhập, không nhận từ client.
          </p>
        </header>
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]">
              <tr>
                <th className="px-4 py-2 text-left">Người dùng</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-right">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.id} className="border-t">
                  <td className="px-4 py-3">
                    <div className="font-medium">{user.email}</div>
                    <div className="text-xs text-[var(--color-muted-fg)]">{user.displayName ?? "—"}</div>
                  </td>
                  <td className="px-4 py-3">
                    {user.isActive ? (
                      <span className="inline-flex items-center gap-1 text-emerald-700"><UserCheck className="size-4" />hoạt động</span>
                    ) : (
                      <span className="inline-flex items-center gap-1 text-red-700"><UserX className="size-4" />đã khoá</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex items-center justify-end gap-3">
                      <Link href="/admin/tenant-agents" className="text-[var(--color-primary)] hover:underline">
                        Quản lý phân quyền
                      </Link>
                      <form action={setActiveAction}>
                        <input type="hidden" name="userId" value={user.id} />
                        <input type="hidden" name="action" value={user.isActive ? "disable" : "enable"} />
                        <Button type="submit" size="sm" variant={user.isActive ? "ghost" : "outline"}>
                          {user.isActive ? "Khoá" : "Mở lại"}
                        </Button>
                      </form>
                    </div>
                  </td>
                </tr>
              ))}
              {users.length === 0 && (
                <tr>
                  <td colSpan={3} className="p-8 text-center text-[var(--color-muted-fg)]">
                    Chưa có người dùng nào trong tenant này.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">Tạo người dùng</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Luôn được tạo trong tenant của bạn với vai trò "user". Không thể tạo tài khoản ADMIN hoặc SUPER_ADMIN.
        </p>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            {ok === "created" ? "Đã tạo người dùng." : ok === "disabled" ? "Đã khoá người dùng." : "Đã mở lại người dùng."}
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <form action={createUserAction} className="space-y-3">
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
          <Button type="submit" className="w-full">
            Tạo người dùng
          </Button>
        </form>
      </aside>
    </div>
  );
}
