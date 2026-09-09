import Link from "next/link";
import { redirect } from "next/navigation";
import { ArrowRight, Bot, CheckCircle2, Plus, Power, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";

// ACL V2-C: ADMIN's own department Agents — CRUD lives entirely under
// /department/agents/**, structurally scoped to the caller's own
// tenant+department by the Gateway (never a client-supplied id). This is a
// genuinely different resource set from /admin/tenant-agents (agents
// entitled TO this department by the tenant owner, not owned BY it) —
// kept as a separate page rather than folded into that one, matching the
// V2 decision to split platform/owner/department semantics cleanly instead
// of adding another role branch to an existing shared page.

interface DepartmentAgentRecord {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  lifecycleState: string;
  enabled: boolean;
  publishedVersionId: string | null;
  scope: "system" | "tenant";
}

async function createAgentAction(formData: FormData): Promise<void> {
  "use server";
  const slug = String(formData.get("slug") ?? "").trim();
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  if (!slug || !name) redirect("/department/agents?error=slug-and-name-required");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentAgent(viewer.token, {
    slug,
    name,
    description: description || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/department/agents?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  const created = (await res.json()) as { id: string };
  redirect(`/department/agents/${created.id}`);
}

function LifecycleBadge({ state }: { state: string }) {
  const isProduction = state === "production";
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
        isProduction ? "border-emerald-300 bg-emerald-50 text-emerald-700" : "border-gray-300 bg-gray-50 text-gray-700"
      }`}
    >
      {isProduction && <ShieldCheck className="size-3" />}
      {state}
    </span>
  );
}

export default async function DepartmentAgentsPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  const { error } = await searchParams;
  const viewer = await requireViewer(["admin"]);
  const res = await gateway.listOwnDepartmentAgents(viewer.token);
  const items: DepartmentAgentRecord[] = res.ok ? ((await res.json()) as { items: DepartmentAgentRecord[] }).items : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Agents</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Một danh sách gồm Agent Template Hệ thống và Agent của phòng ban. Template Hệ thống chỉ
          đọc/tái sử dụng; Agent Phòng ban có đầy đủ thao tác cấu hình, publish và gán nhân sự.
        </p>
      </header>

      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {decodeURIComponent(error)}
        </p>
      )}

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="flex items-center gap-2 font-serif text-lg">
          <Plus className="size-4" /> Tạo Agent mới
        </h2>
        <form action={createAgentAction} className="grid gap-3 sm:grid-cols-3">
          <div className="space-y-1.5">
            <Label htmlFor="c-slug">Slug</Label>
            <Input id="c-slug" name="slug" required placeholder="ke-toan-truong" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="c-name">Tên</Label>
            <Input id="c-name" name="name" required placeholder="Trợ lý kế toán trưởng" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="c-desc">Mô tả (tuỳ chọn)</Label>
            <Input id="c-desc" name="description" placeholder="..." />
          </div>
          <div className="sm:col-span-3">
            <Button type="submit" size="sm">
              Tạo draft
            </Button>
          </div>
        </form>
      </section>

      <section className="space-y-3">
        <h2 className="font-serif text-lg">Agent khả dụng ({items.length})</h2>
        {items.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-12 text-center text-sm text-[var(--color-muted-fg)]">
            Chưa có agent nào. Tạo agent đầu tiên ở trên.
          </div>
        ) : (
          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-4 py-2 text-left">Agent</th>
                  <th className="px-4 py-2 text-left">Trạng thái</th>
                  <th className="px-4 py-2 text-left">Khả dụng</th>
                  <th className="px-4 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {items.map((a) => (
                  <tr key={a.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      {a.scope === "system" ? <span className="inline-flex items-center gap-1.5 font-medium"><Bot className="size-3.5" /> {a.name}</span> : <Link href={`/department/agents/${a.id}`} className="inline-flex items-center gap-1.5 font-medium hover:underline"><Bot className="size-3.5" /> {a.name}</Link>}
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.slug}</div>
                      <div className="mt-1"><ResourceScopeBadge scope={a.scope === "system" ? "platform_template" : "department"} /></div>
                    </td>
                    <td className="px-4 py-3">
                      <LifecycleBadge state={a.lifecycleState} />
                    </td>
                    <td className="px-4 py-3">
                      {a.enabled ? (
                        <span className="inline-flex items-center gap-1 text-emerald-700">
                          <CheckCircle2 className="size-3" /> hoạt động
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 text-red-700">
                          <Power className="size-3" /> tắt
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      {a.scope === "tenant" ? <Link
                        href={`/department/agents/${a.id}`}
                        className="inline-flex items-center gap-1 text-xs text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
                      >
                        Mở <ArrowRight className="size-3" />
                      </Link> : <span className="text-xs text-[var(--color-muted-fg)]">Chỉ đọc</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
