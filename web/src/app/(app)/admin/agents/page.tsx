import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { AlertCircle, ArrowRight, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface AgentRecord {
  id: string;
  tenantId: string;
  code: string;
  name: string;
  type: string;
  lifecycleState: string;
  ownerUserId: string | null;
  actionTier: string;
  disabledAt: string | null;
  updatedAt: string;
}

const LIFECYCLE_BADGES: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  testing: "border-blue-300 bg-blue-50 text-blue-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
  deprecated: "border-orange-300 bg-orange-50 text-orange-700",
  retired: "border-gray-300 bg-gray-100 text-gray-500",
};

async function createAgentAction(formData: FormData): Promise<void> {
  "use server";
  const code = String(formData.get("code") ?? "").trim().toLowerCase();
  const name = String(formData.get("name") ?? "").trim();
  const type = String(formData.get("type") ?? "domain").trim();
  const description = String(formData.get("description") ?? "").trim();
  const actionTier = String(formData.get("actionTier") ?? "suggest").trim();

  if (!code || !name) {
    redirect("/admin/agents?error=missing-fields");
  }
  const token = await requireSession();
  const res = await gateway.createAgent(token, {
    code,
    name,
    type,
    description: description || undefined,
    actionTier,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/agents");
  redirect("/admin/agents?ok=created");
}

export default async function AdminAgentsPage({
  searchParams,
}: {
  searchParams: Promise<{ state?: string; ok?: string; error?: string }>;
}) {
  const { state, ok, error } = await searchParams;
  const token = await requireSession();
  const res = await gateway.listAgents(token, state);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: AgentRecord[]; total: number })
    : { items: [], total: 0 };

  const stateFilters = [
    ["all", "Tất cả"],
    ["draft", "Draft"],
    ["testing", "Testing"],
    ["pending_review", "Pending Review"],
    ["production", "Production"],
    ["deprecated", "Deprecated"],
    ["retired", "Retired"],
  ] as const;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header className="space-y-2">
          <h1 className="font-serif text-3xl">Agents</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Quản lý các agent trong tenant đang active. Mỗi agent có owner, lifecycle state, ACL riêng.
          </p>
          <div className="flex flex-wrap gap-1 rounded-full border border-[var(--color-border)] p-1 text-xs">
            {stateFilters.map(([key, label]) => {
              const href = key === "all" ? "/admin/agents" : `/admin/agents?state=${key}`;
              const active = (state ?? "all") === key;
              return (
                <Link
                  key={key}
                  href={href}
                  className={`rounded-full px-3 py-1.5 transition ${
                    active
                      ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                      : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                  }`}
                >
                  {label}
                </Link>
              );
            })}
          </div>
        </header>

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Agent</th>
                <th className="px-4 py-2 text-left">Type</th>
                <th className="px-4 py-2 text-left">Lifecycle</th>
                <th className="px-4 py-2 text-left">Tier</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-8 text-center text-[var(--color-muted-fg)]">
                    Chưa có agent.
                  </td>
                </tr>
              ) : (
                body.items.map((a) => (
                  <tr key={a.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <Link href={`/admin/agents/${a.id}`} className="font-medium hover:underline">
                        {a.name}
                      </Link>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.code}</div>
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">{a.type}</td>
                    <td className="px-4 py-3">
                      <span
                        className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
                          LIFECYCLE_BADGES[a.lifecycleState] ?? "border-gray-300"
                        }`}
                      >
                        {a.lifecycleState === "production" && <ShieldCheck className="size-3" />}
                        {a.lifecycleState}
                      </span>
                      {a.disabledAt && (
                        <div className="mt-1 inline-flex items-center gap-1 rounded-full bg-red-50 px-2 py-0.5 text-[10px] text-red-700">
                          <AlertCircle className="size-3" /> killed
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs">{a.actionTier}</td>
                    <td className="px-4 py-3 text-right">
                      <Link
                        href={`/admin/agents/${a.id}`}
                        className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                      >
                        Chi tiết <ArrowRight className="size-3" />
                      </Link>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">Tạo agent mới</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Agent mới bắt đầu ở lifecycle <code>draft</code>. Bạn sẽ tự động là <em>owner</em>.
        </p>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            Đã tạo agent.
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <form action={createAgentAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="a-code">Code</Label>
            <Input id="a-code" name="code" required placeholder="invoice-extractor" pattern="^[a-z0-9-]+$" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="a-name">Tên</Label>
            <Input id="a-name" name="name" required placeholder="Invoice Extractor" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="a-type">Type</Label>
            <select
              id="a-type"
              name="type"
              className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              defaultValue="domain"
            >
              <option value="orchestrator">Orchestrator</option>
              <option value="domain">Domain</option>
              <option value="sub">Sub-agent</option>
              <option value="tool_only">Tool-only</option>
            </select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="a-tier">Action Tier</Label>
            <select
              id="a-tier"
              name="actionTier"
              className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              defaultValue="suggest"
            >
              <option value="read_only">Read-only</option>
              <option value="suggest">Suggest</option>
              <option value="execute_with_approval">Execute with approval</option>
              <option value="autonomous">Autonomous</option>
            </select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="a-desc">Mô tả</Label>
            <textarea
              id="a-desc"
              name="description"
              rows={3}
              className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              placeholder="Agent này làm gì..."
            />
          </div>
          <Button type="submit" className="w-full">
            Tạo agent (draft)
          </Button>
        </form>
      </aside>
    </div>
  );
}
