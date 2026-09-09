import { ShieldCheck, Wrench } from "lucide-react";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";

interface ToolRecord {
  id: string;
  code: string;
  name: string;
  description: string | null;
  category: string;
  riskTier: string;
  isActive: boolean;
}

export default async function DepartmentToolsPage() {
  const viewer = await requireViewer(["admin"]);
  const response = await gateway.listTools(viewer.token, true);
  const tools: ToolRecord[] = response.ok
    ? ((await response.json()) as { items: ToolRecord[] }).items
    : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Tools</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Công cụ thực thi đã được nền tảng phê duyệt. Bạn có thể chọn và gắn chúng vào Agent của phòng ban, nhưng không thể sửa implementation, policy hoặc credentials.
        </p>
      </header>
      <section className="grid gap-3 md:grid-cols-2">
        {tools.map((tool) => (
          <article key={tool.id} className="rounded-[var(--radius)] border p-4">
            <div className="flex items-start justify-between gap-3">
              <div>
                <h2 className="flex items-center gap-2 font-medium"><Wrench className="size-4" />{tool.name}</h2>
                <p className="font-mono text-[10px] text-[var(--color-muted-fg)]">{tool.code}</p>
              </div>
              <ResourceScopeBadge scope="platform" />
            </div>
            {tool.description && <p className="mt-2 text-xs text-[var(--color-muted-fg)]">{tool.description}</p>}
            <p className="mt-3 flex items-center gap-1 text-xs text-emerald-700"><ShieldCheck className="size-3" />Đã phê duyệt · risk {tool.riskTier}</p>
          </article>
        ))}
        {tools.length === 0 && <p className="text-sm text-[var(--color-muted-fg)]">Chưa có Tool được phê duyệt.</p>}
      </section>
    </div>
  );
}
