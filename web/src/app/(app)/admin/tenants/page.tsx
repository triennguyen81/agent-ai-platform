import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface TenantRecord {
  id: string;
  code: string;
  name: string;
  status: string;
  createdAt: string;
}

async function createTenantAction(formData: FormData): Promise<void> {
  "use server";
  const code = String(formData.get("code") ?? "").trim().toLowerCase();
  const name = String(formData.get("name") ?? "").trim();
  if (!code || !name) {
    redirect("/admin/tenants?error=missing-fields");
  }
  const token = await requireSession();
  const res = await gateway.createTenant(token, { code, name });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenants?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenants");
  redirect("/admin/tenants?ok=created");
}

function formatDate(s: string): string {
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

export default async function AdminTenantsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.listTenants(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: TenantRecord[]; total: number })
    : { items: [], total: 0 };
  const { ok, error } = await searchParams;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Tenants</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Tổng {body.total} tenants. Mỗi tenant là 1 organization có agents + dữ liệu riêng.
          </p>
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Code</th>
                <th className="px-4 py-2 text-left">Tên</th>
                <th className="px-4 py-2 text-left">Status</th>
                <th className="px-4 py-2 text-left">Tạo</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                    Chưa có tenant nào.
                  </td>
                </tr>
              ) : (
                body.items.map((t) => (
                  <tr key={t.id} className="border-t align-top">
                    <td className="px-4 py-3 font-mono text-xs">{t.code}</td>
                    <td className="px-4 py-3 font-medium">{t.name}</td>
                    <td className="px-4 py-3">
                      <span className="rounded-full border px-2 py-0.5 text-[10px]">{t.status}</span>
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                      {formatDate(t.createdAt)}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">Tạo tenant mới</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Code: chỉ chữ thường, số, dấu gạch ngang. Dùng làm slug bất biến.
        </p>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            Đã tạo tenant.
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <form action={createTenantAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="t-code">Code</Label>
            <Input id="t-code" name="code" required placeholder="partner-abc" pattern="^[a-z0-9-]+$" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-name">Tên hiển thị</Label>
            <Input id="t-name" name="name" required placeholder="Đối tác ABC" />
          </div>
          <Button type="submit" className="w-full">
            Tạo tenant
          </Button>
        </form>
      </aside>
    </div>
  );
}
