import Link from "next/link";
import { redirect } from "next/navigation";
import { ArrowRight, Bot, CheckCircle2, FlaskConical, Power, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";

// Trợ lý AI — operational catalog, not a second Agent Builder. Creating,
// configuring, publishing, and versioning system agents happens exclusively
// in Agent Builder (SUPER_ADMIN, /admin/agent-builder). This page only
// browses what already exists and links out to the right place to act on it.

interface SystemAgentRecord {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  lifecycleState: string;
  enabled: boolean;
  publishedVersionId: string | null;
}

interface TenantAssignedAgentRecord {
  agentId: string;
  slug: string;
  name: string;
  description: string | null;
  lifecycleState: string;
  enabled: boolean;
  assignedAt: string;
}

interface LegacyAgentRecord {
  id: string;
  code: string;
  name: string;
  type: string;
  lifecycleState: string;
  actionTier: string;
  disabledAt: string | null;
}

const LIFECYCLE_BADGES: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  testing: "border-blue-300 bg-blue-50 text-blue-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
  deprecated: "border-orange-300 bg-orange-50 text-orange-700",
  retired: "border-gray-300 bg-gray-100 text-gray-500",
};

function LifecycleBadge({ state }: { state: string }) {
  return (
    <span className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${LIFECYCLE_BADGES[state] ?? "border-gray-300"}`}>
      {state === "production" && <ShieldCheck className="size-3" />}
      {state}
    </span>
  );
}

function AvailabilityBadge({ enabled }: { enabled: boolean }) {
  return enabled ? (
    <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-[10px] text-emerald-700">
      <CheckCircle2 className="size-3" /> khả dụng
    </span>
  ) : (
    <span className="inline-flex items-center gap-1 rounded-full bg-red-50 px-2 py-0.5 text-[10px] text-red-700">
      <Power className="size-3" /> tắt
    </span>
  );
}

export default async function TroLyAiCatalogPage() {
  const viewer = await requireViewer(["super_admin", "tenant_owner", "admin"]);
  const isSuperAdmin = viewer.roles.includes("super_admin");

  if (isSuperAdmin) {
    const res = await gateway.listSystemAgents(viewer.token);
    if (res.status === 403) redirect("/chat");
    const items: SystemAgentRecord[] = res.ok ? ((await res.json()) as { items: SystemAgentRecord[] }).items : [];

    return (
      <div className="space-y-4">
        <header className="space-y-2">
          <h1 className="font-serif text-3xl">Trợ lý AI</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Danh mục vận hành toàn hệ thống — tổng {items.length} trợ lý. Để tạo mới, cấu hình prompt/model,
            publish version, hoặc gán tenant, dùng{" "}
            <Link href="/admin/agent-builder" className="text-[var(--color-primary)] hover:underline">
              Agent Builder
            </Link>
            .
          </p>
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Trợ lý</th>
                <th className="px-4 py-2 text-left">Version</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-left">Khả dụng</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {items.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-8 text-center text-[var(--color-muted-fg)]">
                    Chưa có trợ lý nào. Tạo trong Agent Builder.
                  </td>
                </tr>
              ) : (
                items.map((a) => (
                  <tr key={a.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{a.name}</div>
                      {a.description && <div className="text-xs text-[var(--color-muted-fg)]">{a.description}</div>}
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.slug}</div>
                    </td>
                    <td className="px-4 py-3 text-xs">
                      {a.publishedVersionId ? (
                        <span className="text-emerald-700">đã publish</span>
                      ) : (
                        <span className="text-[var(--color-muted-fg)]">chưa publish</span>
                      )}
                    </td>
                    <td className="px-4 py-3"><LifecycleBadge state={a.lifecycleState} /></td>
                    <td className="px-4 py-3"><AvailabilityBadge enabled={a.enabled} /></td>
                    <td className="px-4 py-3 text-right">
                      <div className="flex justify-end gap-3">
                        <Link
                          href={`/admin/agent-builder/${a.id}?tab=test`}
                          className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                        >
                          <FlaskConical className="size-3" /> Test
                        </Link>
                        <Link
                          href={`/admin/agent-builder/${a.id}`}
                          className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                        >
                          Mở <ArrowRight className="size-3" />
                        </Link>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  // ADMIN: tenant-scoped catalog. Dynamic agents assigned to this tenant,
  // plus any legacy tenant-owned agents from before Agent Builder existed.
  // tenantId is always derived server-side — never requested here.
  const [dynamicRes, legacyRes] = await Promise.all([
    gateway.listTenantAgents(viewer.token),
    gateway.listAgents(viewer.token),
  ]);
  const dynamicItems: TenantAssignedAgentRecord[] = dynamicRes.ok
    ? ((await dynamicRes.json()) as { items: TenantAssignedAgentRecord[] }).items
    : [];
  const legacyItems: LegacyAgentRecord[] = legacyRes.ok
    ? ((await legacyRes.json()) as { items: LegacyAgentRecord[] }).items
    : [];

  return (
    <div className="space-y-8">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Trợ lý AI</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Các trợ lý đang khả dụng cho tenant của bạn. Để gán cho user hoặc quản lý kiến thức, xem{" "}
          <Link href="/admin/tenant-agents" className="text-[var(--color-primary)] hover:underline">
            Tenant Agents
          </Link>
          .
        </p>
      </header>

      <section className="space-y-3">
        <h2 className="flex items-center gap-2 font-serif text-lg">
          <Bot className="size-4" /> Trợ lý hệ thống ({dynamicItems.length})
        </h2>
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Trợ lý</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-left">Khả dụng</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {dynamicItems.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-4 py-6 text-center text-[var(--color-muted-fg)]">
                    Tenant chưa được gán trợ lý nào.
                  </td>
                </tr>
              ) : (
                dynamicItems.map((a) => (
                  <tr key={a.agentId} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{a.name}</div>
                      {a.description && <div className="text-xs text-[var(--color-muted-fg)]">{a.description}</div>}
                    </td>
                    <td className="px-4 py-3"><LifecycleBadge state={a.lifecycleState} /></td>
                    <td className="px-4 py-3"><AvailabilityBadge enabled={a.enabled} /></td>
                    <td className="px-4 py-3 text-right">
                      <Link
                        href={`/admin/tenant-agents/${a.agentId}`}
                        className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                      >
                        Quản lý <ArrowRight className="size-3" />
                      </Link>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      {legacyItems.length > 0 && (
        <section className="space-y-3">
          <h2 className="font-serif text-lg">Trợ lý legacy ({legacyItems.length})</h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Agent tạo trước khi có Agent Builder — vẫn dùng ACL/lifecycle riêng, không có version/publish.
          </p>
          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-4 py-2 text-left">Trợ lý</th>
                  <th className="px-4 py-2 text-left">Trạng thái</th>
                  <th className="px-4 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {legacyItems.map((a) => (
                  <tr key={a.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{a.name}</div>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.code}</div>
                    </td>
                    <td className="px-4 py-3">
                      <LifecycleBadge state={a.lifecycleState} />
                      {a.disabledAt && <div className="mt-1"><AvailabilityBadge enabled={false} /></div>}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <Link
                        href={`/admin/agents/${a.id}`}
                        className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                      >
                        Chi tiết <ArrowRight className="size-3" />
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}
