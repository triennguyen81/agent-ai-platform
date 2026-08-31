import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowLeft, Bot, ShieldCheck, Users } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/input";

interface TenantSummary {
  id: string; code: string; name: string; status: string;
  ownerUserId: string | null; ownerEmail: string | null;
  ownerDisplayName: string | null; ownerIsActive: boolean | null;
  adminCount: number; userCount: number; agentCount: number;
}

interface AdminRecord { id: string; email: string; displayName: string | null; isActive: boolean }

async function transferOwnerAction(formData: FormData): Promise<void> {
  "use server";
  const tenantId = String(formData.get("tenantId") ?? "").trim();
  const successorUserId = String(formData.get("successorUserId") ?? "").trim();
  const predecessorAction = String(formData.get("predecessorAction") ?? "");
  if (!tenantId || !successorUserId || !["demote", "deactivate"].includes(predecessorAction)) {
    redirect(`/admin/tenants/${tenantId}?error=invalid-owner-transfer`);
  }
  const token = await requireSession();
  const response = await gateway.transferTenantOwner(token, tenantId, {
    successorUserId,
    predecessorAction: predecessorAction as "demote" | "deactivate",
  });
  if (!response.ok) {
    const body = await response.text();
    redirect(`/admin/tenants/${tenantId}?error=${encodeURIComponent(body.slice(0, 240))}`);
  }
  revalidatePath("/admin/tenants");
  revalidatePath(`/admin/tenants/${tenantId}`);
  redirect(`/admin/tenants/${tenantId}?ok=owner-transferred`);
}

export default async function TenantDetailPage({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { id } = await params;
  const { ok, error } = await searchParams;
  const token = await requireSession();
  const [summaryResponse, adminsResponse] = await Promise.all([
    gateway.getTenantSummary(token, id),
    gateway.listTenantAdmins(token, id),
  ]);
  if (summaryResponse.status === 404) return <p className="text-sm text-red-700">Tenant không tồn tại.</p>;
  if (!summaryResponse.ok) redirect("/admin/tenants?error=tenant-summary-unavailable");
  const tenant = (await summaryResponse.json()) as TenantSummary;
  const admins: AdminRecord[] = adminsResponse.ok ? ((await adminsResponse.json()) as { items: AdminRecord[] }).items : [];
  const activeSuccessors = admins.filter((admin) => admin.isActive);

  return (
    <div className="space-y-6">
      <Link href="/admin/tenants" className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]">
        <ArrowLeft className="size-3.5" /> Tenants
      </Link>
      <header className="space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{tenant.name}</h1>
          <span className="rounded-full border px-2 py-0.5 text-[10px]">{tenant.status}</span>
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{tenant.code}</p>
      </header>

      {ok && <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">{ok === "onboarded" ? "Đã tạo Tenant + Tenant Owner." : "Đã transfer Tenant Owner."}</p>}
      {error && <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{decodeURIComponent(error)}</p>}

      <section className="space-y-5 rounded-[var(--radius)] border bg-[var(--color-bg)] p-6">
        <header>
          <h2 className="flex items-center gap-2 font-serif text-xl"><ShieldCheck className="size-5" /> Tenant Owner</h2>
          <p className="text-sm text-[var(--color-muted-fg)]">Chỉ SUPER_ADMIN có thể assign hoặc transfer owner. Active tenant luôn có đúng một active Tenant Owner.</p>
        </header>
        <dl className="grid gap-4 sm:grid-cols-3">
          <div><dt className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">Tên hiển thị</dt><dd className="mt-1 font-medium">{tenant.ownerDisplayName ?? "—"}</dd></div>
          <div><dt className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">Email</dt><dd className="mt-1">{tenant.ownerEmail ?? "—"}</dd></div>
          <div><dt className="text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">Status</dt><dd className={`mt-1 ${tenant.ownerIsActive ? "text-emerald-700" : "text-red-700"}`}>{tenant.ownerIsActive ? "active" : "inactive / missing"}</dd></div>
        </dl>
        <div className="border-t pt-4">
          <h3 className="font-medium">Transfer Owner</h3>
          <p className="mb-3 text-xs text-[var(--color-muted-fg)]">Chọn một active ADMIN thuộc tenant làm owner kế nhiệm. Thao tác được thực hiện atomically.</p>
          {activeSuccessors.length > 0 ? (
            <form action={transferOwnerAction} className="grid gap-3 sm:grid-cols-[1fr_220px_auto] sm:items-end">
              <input type="hidden" name="tenantId" value={tenant.id} />
              <div className="space-y-1.5">
                <Label htmlFor="successor">Owner kế nhiệm</Label>
                <select id="successor" name="successorUserId" required className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-3 py-2 text-sm">
                  <option value="">— Chọn administrator —</option>
                  {activeSuccessors.map((admin) => <option key={admin.id} value={admin.id}>{admin.displayName ?? admin.email} · {admin.email}</option>)}
                </select>
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="predecessor-action">Owner hiện tại</Label>
                <select id="predecessor-action" name="predecessorAction" className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-3 py-2 text-sm">
                  <option value="demote">Chuyển thành ADMIN</option><option value="deactivate">Vô hiệu hoá</option>
                </select>
              </div>
              <Button type="submit">Transfer Owner</Button>
            </form>
          ) : <p className="text-sm text-[var(--color-muted-fg)]">Chưa có active ADMIN để chọn. TENANT_OWNER hiện tại cần tạo ADMIN từ trang Administrators trước khi SUPER_ADMIN transfer ownership.</p>}
        </div>
      </section>

      <section className="grid gap-4 sm:grid-cols-3">
        <div className="rounded-[var(--radius)] border p-5"><ShieldCheck className="mb-3 size-5" /><div className="text-3xl font-semibold tabular-nums">{tenant.adminCount}</div><div className="text-sm text-[var(--color-muted-fg)]">Administrators</div></div>
        <div className="rounded-[var(--radius)] border p-5"><Users className="mb-3 size-5" /><div className="text-3xl font-semibold tabular-nums">{tenant.userCount}</div><div className="text-sm text-[var(--color-muted-fg)]">Users</div></div>
        <div className="rounded-[var(--radius)] border p-5"><Bot className="mb-3 size-5" /><div className="text-3xl font-semibold tabular-nums">{tenant.agentCount}</div><div className="text-sm text-[var(--color-muted-fg)]">Assigned Agents</div></div>
      </section>
      <p className="text-sm text-[var(--color-muted-fg)]">ADMIN provisioning is owned by this tenant&apos;s TENANT_OWNER. SUPER_ADMIN normally manages only tenant onboarding, ownership, quota, status, and platform-agent assignment.</p>
    </div>
  );
}
