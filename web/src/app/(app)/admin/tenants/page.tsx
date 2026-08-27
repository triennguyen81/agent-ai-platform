import Link from "next/link";
import { ArrowRight } from "lucide-react";
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

interface TenantQuotaStatus {
  tenantId: string;
  maxUsers: number | null;
  currentUsers: number;
  maxAgents: number | null;
  currentAgents: number;
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

function parseLimit(formData: FormData, field: string): number | null {
  const raw = String(formData.get(field) ?? "").trim();
  return raw === "" ? null : Number(raw);
}

async function updateQuotaAction(formData: FormData): Promise<void> {
  "use server";
  const tenantId = String(formData.get("tenantId") ?? "").trim();
  if (!tenantId) redirect("/admin/tenants?error=missing-tenant");
  const maxUsers = parseLimit(formData, "maxUsers");
  const maxAgents = parseLimit(formData, "maxAgents");
  if ((maxUsers !== null && (!Number.isInteger(maxUsers) || maxUsers < 0)) ||
      (maxAgents !== null && (!Number.isInteger(maxAgents) || maxAgents < 0))) {
    redirect("/admin/tenants?error=quota-must-be-a-non-negative-integer-or-blank-for-unlimited");
  }
  const token = await requireSession();
  const res = await gateway.updateTenantQuota(token, tenantId, { maxUsers, maxAgents });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenants?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenants");
  redirect("/admin/tenants?ok=quota-updated");
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

  const quotaResults = await Promise.all(
    body.items.map((t) => gateway.getTenantQuota(token, t.id).then((r) => (r.ok ? (r.json() as Promise<TenantQuotaStatus>) : null))),
  );
  const quotaByTenant = new Map(body.items.map((t, i) => [t.id, quotaResults[i]]));

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
                <th className="px-4 py-2 text-left">Quota (users / agents)</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                    Chưa có tenant nào.
                  </td>
                </tr>
              ) : (
                body.items.map((t) => {
                  const q = quotaByTenant.get(t.id);
                  return (
                    <tr key={t.id} className="border-t align-top">
                      <td className="px-4 py-3 font-mono text-xs">{t.code}</td>
                      <td className="px-4 py-3">
                        <Link href={`/admin/tenants/${t.id}`} className="font-medium hover:underline">
                          {t.name}
                        </Link>
                      </td>
                      <td className="px-4 py-3">
                        <span className="rounded-full border px-2 py-0.5 text-[10px]">{t.status}</span>
                      </td>
                      <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                        {formatDate(t.createdAt)}
                      </td>
                      <td className="px-4 py-3">
                        {q ? (
                          <form action={updateQuotaAction} className="flex items-center gap-2">
                            <input type="hidden" name="tenantId" value={t.id} />
                            <span className="text-xs text-[var(--color-muted-fg)]">{q.currentUsers}/</span>
                            <input
                              name="maxUsers"
                              type="number"
                              min={0}
                              defaultValue={q.maxUsers ?? ""}
                              placeholder="∞"
                              className="w-14 rounded border bg-[var(--color-bg)] px-1 py-0.5 text-xs"
                            />
                            <span className="text-xs text-[var(--color-muted-fg)]">· {q.currentAgents}/</span>
                            <input
                              name="maxAgents"
                              type="number"
                              min={0}
                              defaultValue={q.maxAgents ?? ""}
                              placeholder="∞"
                              className="w-14 rounded border bg-[var(--color-bg)] px-1 py-0.5 text-xs"
                            />
                            <Button type="submit" size="sm" variant="ghost">
                              Lưu
                            </Button>
                          </form>
                        ) : (
                          <span className="text-xs text-[var(--color-muted-fg)]">—</span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-right">
                        <Link
                          href={`/admin/tenants/${t.id}`}
                          className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                        >
                          Chi tiết <ArrowRight className="size-3" />
                        </Link>
                      </td>
                    </tr>
                  );
                })
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
            {ok === "quota-updated" ? "Đã cập nhật quota." : "Đã tạo tenant."}
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
