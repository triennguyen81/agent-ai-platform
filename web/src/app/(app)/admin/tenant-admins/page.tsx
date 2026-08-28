import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ShieldCheck, UserCheck, UserX } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface TenantAdmin {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
}

async function createAdminAction(formData: FormData): Promise<void> {
  "use server";
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  const displayName = String(formData.get("displayName") ?? "").trim() || undefined;
  if (!email || password.length < 8) redirect("/admin/tenant-admins?error=invalid-input");
  const token = await requireSession();
  const response = await gateway.createOwnTenantAdmin(token, { email, password, displayName });
  if (!response.ok) {
    const body = await response.text();
    redirect(`/admin/tenant-admins?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-admins");
  redirect("/admin/tenant-admins?ok=created");
}

async function setActiveAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "");
  const isActive = String(formData.get("isActive") ?? "") === "true";
  if (!userId) redirect("/admin/tenant-admins?error=invalid-input");
  const token = await requireSession();
  const response = await gateway.setOwnTenantAdminActive(token, userId, isActive);
  if (!response.ok) redirect(`/admin/tenant-admins?error=update-failed-${response.status}`);
  revalidatePath("/admin/tenant-admins");
  redirect(`/admin/tenant-admins?ok=${isActive ? "enabled" : "disabled"}`);
}

export default async function TenantAdministratorsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const viewer = await requireViewer(["tenant_owner"]);
  const response = await gateway.listOwnTenantAdmins(viewer.token);
  const admins: TenantAdmin[] = response.ok
    ? ((await response.json()) as { items: TenantAdmin[] }).items
    : [];
  const { ok, error } = await searchParams;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Administrators</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Quản trị viên của tenant hiện tại. Tenant được xác định từ phiên đăng nhập.
          </p>
        </header>
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]"><tr><th className="px-4 py-2 text-left">Administrator</th><th className="px-4 py-2 text-left">Trạng thái</th><th className="px-4 py-2 text-right">Thao tác</th></tr></thead>
            <tbody>
              {admins.map((admin) => (
                <tr key={admin.id} className="border-t">
                  <td className="px-4 py-3"><div className="font-medium">{admin.email}</div><div className="text-xs text-[var(--color-muted-fg)]">{admin.displayName ?? "—"}</div></td>
                  <td className="px-4 py-3">{admin.isActive ? <span className="inline-flex items-center gap-1 text-emerald-700"><UserCheck className="size-4" />hoạt động</span> : <span className="inline-flex items-center gap-1 text-red-700"><UserX className="size-4" />đã khoá</span>}</td>
                  <td className="px-4 py-3 text-right">
                    <form action={setActiveAction}>
                      <input type="hidden" name="userId" value={admin.id} />
                      <input type="hidden" name="isActive" value={String(!admin.isActive)} />
                      <Button type="submit" size="sm" variant={admin.isActive ? "ghost" : "outline"}>{admin.isActive ? "Khoá" : "Mở lại"}</Button>
                    </form>
                  </td>
                </tr>
              ))}
              {admins.length === 0 && <tr><td colSpan={3} className="p-8 text-center text-[var(--color-muted-fg)]">Tenant chưa có ADMIN.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl"><ShieldCheck className="mr-2 inline size-5" />Tạo ADMIN</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">Tài khoản luôn thuộc tenant hiện tại và luôn có vai trò ADMIN. Chỉ TENANT_OWNER truy cập được thao tác này.</p>
        {ok && <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">Thao tác thành công.</p>}
        {error && <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{decodeURIComponent(error)}</p>}
        <form action={createAdminAction} className="space-y-3">
          <div className="space-y-1.5"><Label htmlFor="a-email">Email</Label><Input id="a-email" name="email" type="email" required /></div>
          <div className="space-y-1.5"><Label htmlFor="a-password">Mật khẩu</Label><Input id="a-password" name="password" type="password" minLength={8} required /></div>
          <div className="space-y-1.5"><Label htmlFor="a-name">Tên hiển thị</Label><Input id="a-name" name="displayName" /></div>
          <Button type="submit" className="w-full">Tạo administrator</Button>
        </form>
      </aside>
    </div>
  );
}
