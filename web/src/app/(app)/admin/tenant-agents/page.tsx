import Link from "next/link";
import { redirect } from "next/navigation";
import { ArrowRight, Bot, Inbox, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

interface TenantAssignedAgentRecord {
  agentId: string;
  slug: string;
  name: string;
  description: string | null;
  lifecycleState: string;
  enabled: boolean;
  assignedAt: string;
}

export default async function TenantAgentsPage() {
  const token = await requireSession();
  const res = await gateway.listTenantAgents(token);
  // A super_admin token has no tenant context — this page is for tenant
  // ADMIN only; send anyone else back rather than showing an empty/broken list.
  if (res.status === 400 || res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: TenantAssignedAgentRecord[] })
    : { items: [] };

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Agents của tenant</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Các agent nền tảng (system agent) đã được SUPER_ADMIN assign cho tenant của bạn. Chọn
          một agent để gán/thu hồi quyền dùng cho từng user trong tenant.
        </p>
      </header>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center">
          <Inbox className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="text-sm text-[var(--color-muted-fg)]">
            Tenant của bạn chưa được assign agent nào. Liên hệ SUPER_ADMIN.
          </p>
        </div>
      ) : (
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Agent</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {body.items.map((a) => (
                <tr key={a.agentId} className="border-t align-top">
                  <td className="px-4 py-3">
                    <Link
                      href={`/admin/tenant-agents/${a.agentId}`}
                      className="inline-flex items-center gap-1.5 font-medium hover:underline"
                    >
                      <Bot className="size-3.5" /> {a.name}
                    </Link>
                    <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.slug}</div>
                  </td>
                  <td className="px-4 py-3 text-xs">
                    {!a.enabled ? (
                      <span className="text-red-700">disabled bởi SUPER_ADMIN</span>
                    ) : a.lifecycleState === "production" ? (
                      <span className="inline-flex items-center gap-1 text-emerald-700">
                        <ShieldCheck className="size-3" /> production
                      </span>
                    ) : (
                      <span className="text-[var(--color-muted-fg)]">chưa publish</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Link
                      href={`/admin/tenant-agents/${a.agentId}`}
                      className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                    >
                      Gán user <ArrowRight className="size-3" />
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
