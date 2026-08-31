import Link from "next/link";
import { ArrowRight } from "lucide-react";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface TenantSummary {
  id: string;
  code: string;
  name: string;
  status: string;
  createdAt: string;
  ownerUserId: string | null;
  ownerEmail: string | null;
  ownerDisplayName: string | null;
  ownerIsActive: boolean | null;
  adminCount: number;
  userCount: number;
  agentCount: number;
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
  const ownerDisplayName = String(formData.get("ownerDisplayName") ?? "").trim();
  const ownerEmail = String(formData.get("ownerEmail") ?? "").trim().toLowerCase();
  const ownerPassword = String(formData.get("ownerPassword") ?? "");
  if (!code || !name || !ownerDisplayName || !ownerEmail || ownerPassword.length < 8) {
    redirect("/admin/tenants?error=missing-fields");
  }
  const token = await requireSession();
  const res = await gateway.onboardTenant(token, {
    code,
    name,
    ownerDisplayName,
    ownerEmail,
    ownerPassword,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenants?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  const created = (await res.json()) as { tenant: { id: string } };
  revalidatePath("/admin/tenants");
  redirect(`/admin/tenants/${created.tenant.id}?ok=onboarded`);
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

export default async function AdminTenantsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.listTenantSummaries(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: TenantSummary[]; total: number })
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
            Tổng {body.total} tenants. Mỗi active tenant có đúng một active Tenant Owner.
          </p>
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Tenant</th>
                <th className="px-4 py-2 text-left">Tenant Owner</th>
                <th className="px-4 py-2 text-center">Admins</th>
                <th className="px-4 py-2 text-center">Users</th>
                <th className="px-4 py-2 text-center">Agents</th>
                <th className="px-4 py-2 text-left">Status</th>
                <th className="px-4 py-2 text-left">Quota (users / agents)</th>
                <th className="px-4 py-2 text-right">Detail</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={8} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                    Chưa có tenant nào.
                  </td>
                </tr>
              ) : (
                body.items.map((t) => {
                  const q = quotaByTenant.get(t.id);
                  return (
                    <tr key={t.id} className="border-t align-top">
                      <td className="px-4 py-3">
                        <Link href={`/admin/tenants/${t.id}`} className="font-medium hover:underline">
                          {t.name}
                        </Link>
                        <div className="font-mono text-xs text-[var(--color-muted-fg)]">{t.code}</div>
                      </td>
                      <td className="px-4 py-3">
                        {t.ownerUserId ? (
                          <>
                            <div className="font-medium">{t.ownerDisplayName ?? t.ownerEmail}</div>
                            <div className="text-xs text-[var(--color-muted-fg)]">{t.ownerEmail}</div>
                          </>
                        ) : <span className="text-xs text-red-700">Chưa có owner</span>}
                      </td>
                      <td className="px-4 py-3 text-center tabular-nums">{t.adminCount}</td>
                      <td className="px-4 py-3 text-center tabular-nums">{t.userCount}</td>
                      <td className="px-4 py-3 text-center tabular-nums">{t.agentCount}</td>
                      <td className="px-4 py-3">
                        <span className="rounded-full border px-2 py-0.5 text-[10px]">{t.status}</span>
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
                          Detail <ArrowRight className="size-3" />
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
        <h2 className="font-serif text-xl">Tạo Tenant + Tenant Owner</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Tenant và owner được tạo atomically. Active tenant không bao giờ tồn tại thiếu owner.
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
          <div className="border-b pb-2 text-xs font-semibold uppercase tracking-wider text-[var(--color-muted-fg)]">
            Thông tin tenant
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-code">Code</Label>
            <Input id="t-code" name="code" required placeholder="partner-abc" pattern="^[a-z0-9-]+$" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-name">Tên hiển thị</Label>
            <Input id="t-name" name="name" required placeholder="Đối tác ABC" />
          </div>
          <div className="border-b pb-2 pt-3 text-xs font-semibold uppercase tracking-wider text-[var(--color-muted-fg)]">
            Tenant Owner
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="owner-name">Tên hiển thị</Label>
            <Input id="owner-name" name="ownerDisplayName" required placeholder="Nguyễn Văn Chủ" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="owner-email">Email</Label>
            <Input id="owner-email" name="ownerEmail" type="email" required placeholder="owner@company.com" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="owner-password">Password</Label>
            <Input id="owner-password" name="ownerPassword" type="password" required minLength={8} placeholder="Tối thiểu 8 ký tự" />
          </div>
          <Button type="submit" className="w-full">
            Tạo Tenant + Tenant Owner
          </Button>
        </form>
      </aside>
    </div>
  );
}
