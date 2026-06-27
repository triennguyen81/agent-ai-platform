import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { Shield, ShieldAlert, ShieldCheck, ShieldOff } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface ToolRecord {
  id: string;
  code: string;
  name: string;
  description: string | null;
  category: string | null;
  riskTier: string;
  minActionTier: string;
  isActive: boolean;
  updatedAt: string;
}

const RISK_BADGES: Record<string, { cls: string; icon: typeof Shield }> = {
  low: { cls: "border-emerald-300 bg-emerald-50 text-emerald-700", icon: Shield },
  medium: { cls: "border-amber-300 bg-amber-50 text-amber-700", icon: ShieldCheck },
  high: { cls: "border-orange-300 bg-orange-50 text-orange-700", icon: ShieldAlert },
  critical: { cls: "border-red-300 bg-red-50 text-red-700", icon: ShieldAlert },
};

async function createToolAction(formData: FormData): Promise<void> {
  "use server";
  const code = String(formData.get("code") ?? "").trim().toLowerCase();
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  const category = String(formData.get("category") ?? "read").trim();
  const riskTier = String(formData.get("riskTier") ?? "low").trim();
  const minActionTier = String(formData.get("minActionTier") ?? "suggest").trim();
  if (!code || !name) redirect("/admin/tools?error=missing-fields");

  const token = await requireSession();
  const res = await gateway.createTool(token, {
    code,
    name,
    description: description || undefined,
    category,
    riskTier,
    minActionTier,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tools?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tools");
  redirect("/admin/tools?ok=created");
}

async function toggleActiveAction(formData: FormData): Promise<void> {
  "use server";
  const toolId = String(formData.get("toolId") ?? "");
  const isActive = String(formData.get("isActive") ?? "") === "true";
  if (!toolId) redirect("/admin/tools");

  const token = await requireSession();
  await gateway.updateTool(token, toolId, { isActive: !isActive });
  revalidatePath("/admin/tools");
  redirect(`/admin/tools?ok=${isActive ? "disabled" : "enabled"}`);
}

export default async function AdminToolsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const token = await requireSession();
  const res = await gateway.listTools(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: ToolRecord[]; total: number })
    : { items: [], total: 0 };
  const { ok, error } = await searchParams;

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Tool Registry</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            {body.total} tools toàn hệ thống. Mỗi agent chỉ gọi được tool đã grant + tier matching.
          </p>
        </header>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            Thành công.
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Tool</th>
                <th className="px-3 py-2 text-left">Risk</th>
                <th className="px-3 py-2 text-left">Min Tier</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-3 py-6 text-center text-[var(--color-muted-fg)]">
                    Chưa có tool. Tạo bằng form bên phải.
                  </td>
                </tr>
              ) : (
                body.items.map((t) => {
                  const badge = RISK_BADGES[t.riskTier] ?? RISK_BADGES.low;
                  const Icon = badge.icon;
                  return (
                    <tr key={t.id} className="border-t align-top">
                      <td className="px-3 py-3">
                        <div className="font-medium">{t.name}</div>
                        <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{t.code}</div>
                        {t.description && (
                          <div className="mt-0.5 text-xs text-[var(--color-muted-fg)]">{t.description}</div>
                        )}
                      </td>
                      <td className="px-3 py-3">
                        <span
                          className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${badge.cls}`}
                        >
                          <Icon className="size-3" /> {t.riskTier}
                        </span>
                        {t.category && (
                          <div className="mt-1 text-[10px] text-[var(--color-muted-fg)]">{t.category}</div>
                        )}
                      </td>
                      <td className="px-3 py-3 text-xs">{t.minActionTier}</td>
                      <td className="px-3 py-3">
                        {t.isActive ? (
                          <span className="text-xs text-emerald-700">active</span>
                        ) : (
                          <span className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)]">
                            <ShieldOff className="size-3" /> disabled
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-3 text-right">
                        <form action={toggleActiveAction}>
                          <input type="hidden" name="toolId" value={t.id} />
                          <input type="hidden" name="isActive" value={String(t.isActive)} />
                          <Button type="submit" size="sm" variant="ghost">
                            {t.isActive ? "Tắt" : "Bật"}
                          </Button>
                        </form>
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
        <h2 className="font-serif text-xl">Thêm tool mới</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Code phải khớp với MCP tool name: <code>mcp__aiq__&lt;tool&gt;</code>
        </p>

        <form action={createToolAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="t-code">Code</Label>
            <Input id="t-code" name="code" required placeholder="mcp__aiq__send_email" pattern="^[a-z0-9_]+$" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-name">Tên</Label>
            <Input id="t-name" name="name" required placeholder="Send email" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-desc">Mô tả</Label>
            <textarea
              id="t-desc"
              name="description"
              rows={2}
              className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
            />
          </div>
          <div className="grid grid-cols-2 gap-2">
            <div className="space-y-1.5">
              <Label htmlFor="t-cat">Category</Label>
              <select
                id="t-cat"
                name="category"
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                defaultValue="read"
              >
                <option value="read">read</option>
                <option value="write">write</option>
                <option value="external">external</option>
              </select>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="t-risk">Risk Tier</Label>
              <select
                id="t-risk"
                name="riskTier"
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                defaultValue="low"
              >
                <option value="low">low</option>
                <option value="medium">medium</option>
                <option value="high">high</option>
                <option value="critical">critical</option>
              </select>
            </div>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="t-tier">Min Action Tier</Label>
            <select
              id="t-tier"
              name="minActionTier"
              className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              defaultValue="suggest"
            >
              <option value="read_only">read_only</option>
              <option value="suggest">suggest</option>
              <option value="execute_with_approval">execute_with_approval</option>
              <option value="autonomous">autonomous</option>
            </select>
          </div>
          <Button type="submit" className="w-full">
            Thêm tool
          </Button>
        </form>
      </aside>
    </div>
  );
}
