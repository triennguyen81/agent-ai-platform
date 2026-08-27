import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowLeft, ArrowRight, ShieldCheck, UserCheck, UserX } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface TenantRecord {
  id: string;
  code: string;
  name: string;
  status: string;
}

interface AdminRecord {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
}

async function createAdminAction(formData: FormData): Promise<void> {
  "use server";
  const tenantId = String(formData.get("tenantId") ?? "").trim();
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  const displayName = String(formData.get("displayName") ?? "").trim() || undefined;
  if (!tenantId) redirect("/admin/tenants");
  if (!email || password.length < 8) {
    redirect(`/admin/tenants/${tenantId}?error=email-va-mat-khau-toi-thieu-8-ky-tu-la-bat-buoc`);
  }
  const token = await requireSession();
  const res = await gateway.createTenantAdmin(token, tenantId, { email, password, displayName });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenants/${tenantId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/tenants/${tenantId}`);
  redirect(`/admin/tenants/${tenantId}?ok=admin-created`);
}

async function setAdminActiveAction(formData: FormData): Promise<void> {
  "use server";
  const tenantId = String(formData.get("tenantId") ?? "").trim();
  const userId = String(formData.get("userId") ?? "").trim();
  const action = String(formData.get("action") ?? "").trim(); // "enable" | "disable"
  if (!tenantId || !userId) redirect("/admin/tenants");
  const token = await requireSession();
  const res = action === "disable"
    ? await gateway.disableTenantAdmin(token, tenantId, userId)
    : await gateway.enableTenantAdmin(token, tenantId, userId);
  if (!res.ok) {
    redirect(`/admin/tenants/${tenantId}?error=${encodeURIComponent(`${action}-that-bai`)}`);
  }
  revalidatePath(`/admin/tenants/${tenantId}`);
  redirect(`/admin/tenants/${tenantId}?ok=${action}d`);
}

export default async function TenantDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { id } = await params;
  const { ok, error } = await searchParams;
  const token = await requireSession();

  const [tenantRes, adminsRes] = await Promise.all([
    gateway.getTenant(token, id),
    gateway.listTenantAdmins(token, id),
  ]);
  if (tenantRes.status === 404) {
    return <p className="text-sm text-red-700">Tenant không tồn tại.</p>;
  }
  const tenant = (await tenantRes.json()) as TenantRecord;
  const admins: AdminRecord[] = adminsRes.ok ? ((await adminsRes.json()) as { items: AdminRecord[] }).items : [];

  return (
    <div className="space-y-6">
      <Link
        href="/admin/tenants"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Tenants
      </Link>

      <header className="space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{tenant.name}</h1>
          <span className="rounded-full border px-2 py-0.5 text-[10px]">{tenant.status}</span>
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{tenant.code}</p>
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
          {ok === "admin-created" ? "Đã tạo administrator." : ok === "disabled" ? "Đã khoá administrator." : ok === "enabled" ? "Đã mở lại administrator." : "Thành công."}
        </p>
      )}
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {decodeURIComponent(error)}
        </p>
      )}

      <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
        <section className="space-y-4">
          <header>
            <h2 className="flex items-center gap-2 font-serif text-xl">
              <ShieldCheck className="size-4" /> Administrators ({admins.length})
            </h2>
            <p className="text-sm text-[var(--color-muted-fg)]">
              Administrator luôn thuộc đúng một tenant — được tạo và gán tenant ở đây, không qua trang Người dùng chung.
            </p>
          </header>

          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-4 py-2 text-left">Email</th>
                  <th className="px-4 py-2 text-left">Trạng thái</th>
                  <th className="px-4 py-2 text-right">Hành động</th>
                </tr>
              </thead>
              <tbody>
                {admins.length === 0 ? (
                  <tr>
                    <td colSpan={3} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                      Tenant chưa có administrator nào.
                    </td>
                  </tr>
                ) : (
                  admins.map((a) => (
                    <tr key={a.id} className="border-t align-top">
                      <td className="px-4 py-3">
                        <div className="font-medium">{a.email}</div>
                        <div className="text-xs text-[var(--color-muted-fg)]">{a.displayName ?? "—"}</div>
                      </td>
                      <td className="px-4 py-3">
                        {a.isActive ? (
                          <span className="inline-flex items-center gap-1 text-emerald-700"><UserCheck className="size-4" />hoạt động</span>
                        ) : (
                          <span className="inline-flex items-center gap-1 text-red-700"><UserX className="size-4" />đã khoá</span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-right">
                        <form action={setAdminActiveAction}>
                          <input type="hidden" name="tenantId" value={tenant.id} />
                          <input type="hidden" name="userId" value={a.id} />
                          <input type="hidden" name="action" value={a.isActive ? "disable" : "enable"} />
                          <Button type="submit" size="sm" variant={a.isActive ? "ghost" : "outline"}>
                            {a.isActive ? "Khoá" : "Mở lại"}
                          </Button>
                        </form>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <p className="text-sm text-[var(--color-muted-fg)]">
            Để gán agent cho tenant này, dùng{" "}
            <Link href="/admin/agent-builder" className="text-[var(--color-primary)] hover:underline">
              Agent Builder <ArrowRight className="inline size-3" />
            </Link>{" "}
            — tab Tenant Assignment trên từng agent.
          </p>
        </section>

        <aside className="space-y-4">
          <h2 className="font-serif text-xl">Tạo administrator</h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Luôn được tạo với vai trò "admin" và gán ngay vào tenant <strong>{tenant.name}</strong>. Không thể tạo SUPER_ADMIN ở đây.
          </p>
          <form action={createAdminAction} className="space-y-3">
            <input type="hidden" name="tenantId" value={tenant.id} />
            <div className="space-y-1.5">
              <Label htmlFor="ad-email">Email</Label>
              <Input id="ad-email" name="email" type="email" required placeholder="admin@company.com" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ad-password">Mật khẩu</Label>
              <Input id="ad-password" name="password" type="password" required minLength={8} placeholder="Tối thiểu 8 ký tự" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ad-name">Tên hiển thị (không bắt buộc)</Label>
              <Input id="ad-name" name="displayName" placeholder="Nguyễn Văn A" />
            </div>
            <Button type="submit" className="w-full">
              Tạo administrator
            </Button>
          </form>
        </aside>
      </div>
    </div>
  );
}
