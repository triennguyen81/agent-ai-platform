import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowRight, Bot, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface SystemAgentRecord {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  type: string;
  actionTier: string;
  lifecycleState: string;
  enabled: boolean;
  currentDraftVersionId: string | null;
  publishedVersionId: string | null;
  updatedAt: string;
}

const LIFECYCLE_BADGES: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
};

async function createSystemAgentAction(formData: FormData): Promise<void> {
  "use server";
  const slug = String(formData.get("slug") ?? "").trim().toLowerCase();
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  const actionTier = String(formData.get("actionTier") ?? "suggest").trim();

  if (!slug || !name) {
    redirect("/admin/agent-builder?error=missing-fields");
  }
  const token = await requireSession();
  const res = await gateway.createSystemAgent(token, {
    slug,
    name,
    description: description || undefined,
    actionTier,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  const created = (await res.json()) as SystemAgentRecord;
  revalidatePath("/admin/agent-builder");
  redirect(`/admin/agent-builder/${created.id}?ok=created`);
}

export default async function AgentBuilderPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const token = await requireSession();
  const res = await gateway.listSystemAgents(token);
  if (res.status === 403) redirect("/chat");

  const body = res.ok
    ? ((await res.json()) as { items: SystemAgentRecord[] })
    : { items: [] };

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header className="space-y-2">
          <h1 className="font-serif text-3xl">Agent Builder</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Tạo và quản lý agent nền tảng (system agent). Mỗi agent chạy động từ cấu
            hình đã publish trong database — không cần sửa code hay rebuild.
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
                <th className="px-4 py-2 text-left">Agent</th>
                <th className="px-4 py-2 text-left">Lifecycle</th>
                <th className="px-4 py-2 text-left">Version</th>
                <th className="px-4 py-2 text-left">Status</th>
                <th className="px-4 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {body.items.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-8 text-center text-[var(--color-muted-fg)]">
                    Chưa có agent nào. Tạo agent đầu tiên ở form bên phải.
                  </td>
                </tr>
              ) : (
                body.items.map((a) => (
                  <tr key={a.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <Link
                        href={`/admin/agent-builder/${a.id}`}
                        className="inline-flex items-center gap-1.5 font-medium hover:underline"
                      >
                        <Bot className="size-3.5" /> {a.name}
                      </Link>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.slug}</div>
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
                          LIFECYCLE_BADGES[a.lifecycleState] ?? "border-gray-300"
                        }`}
                      >
                        {a.lifecycleState === "production" && <ShieldCheck className="size-3" />}
                        {a.lifecycleState}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">
                      {a.currentDraftVersionId ? "draft pending" : a.publishedVersionId ? "published" : "—"}
                    </td>
                    <td className="px-4 py-3 text-xs">
                      {a.enabled ? (
                        <span className="text-emerald-700">enabled</span>
                      ) : (
                        <span className="text-red-700">disabled</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <Link
                        href={`/admin/agent-builder/${a.id}`}
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
          Agent mới bắt đầu ở lifecycle <code>draft</code>, chưa có version nào. Bước
          kế tiếp là cấu hình draft (provider/model/prompt) trong trang chi tiết.
        </p>

        <form action={createSystemAgentAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="s-slug">Slug</Label>
            <Input id="s-slug" name="slug" required placeholder="ke-toan-viet" pattern="^[a-z0-9-]+$" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="s-name">Tên</Label>
            <Input id="s-name" name="name" required placeholder="Trợ lý Kế toán" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="s-tier">Action Tier</Label>
            <select
              id="s-tier"
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
            <Label htmlFor="s-desc">Mô tả</Label>
            <textarea
              id="s-desc"
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
