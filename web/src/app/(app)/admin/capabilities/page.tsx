import { ListChecks } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";

// D7 §3: SUPER_ADMIN platform IA needs a "Nghiệp vụ Catalog" entry point.
// Read-only — the catalog itself has no create/update/delete UI or endpoint
// (D4 seeds it via migrations); this page only surfaces the existing
// GET /system/capabilities data so SUPER_ADMIN can see what codes exist and
// which agent versions declare them, without going through Agent Builder.
interface CapabilityRecord {
  id: string;
  code: string;
  name: string;
  description: string | null;
  category: string | null;
  isActive: boolean;
}

export default async function CapabilityCatalogPage() {
  const viewer = await requireViewer(["super_admin"]);
  const res = await gateway.listCapabilityCatalog(viewer.token);
  const items: CapabilityRecord[] = res.ok
    ? ((await res.json()) as { items: CapabilityRecord[] }).items
    : [];

  const byCategory = items.reduce<Record<string, CapabilityRecord[]>>((acc, item) => {
    const key = item.category ?? "Khác";
    (acc[key] ??= []).push(item);
    return acc;
  }, {});

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="flex items-center gap-2 font-serif text-3xl">
          <ListChecks className="size-6" /> Nghiệp vụ Catalog
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Danh mục nghiệp vụ (business capability) toàn nền tảng — mỗi mã nghiệp vụ được một
          hoặc nhiều agent version khai báo hỗ trợ (Agent Builder → Nghiệp vụ hỗ trợ), và được
          Tenant Owner gán cho phòng ban (Departments → Nghiệp vụ). Danh mục này chỉ để xem;
          thêm/sửa mã nghiệp vụ mới thực hiện qua migration, không có UI tạo/sửa/xoá.
        </p>
      </header>

      {items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center text-sm text-[var(--color-muted-fg)]">
          Chưa có nghiệp vụ nào trong danh mục.
        </div>
      ) : (
        Object.entries(byCategory).map(([category, records]) => (
          <section key={category} className="space-y-3">
            <h2 className="font-serif text-lg capitalize">{category}</h2>
            <div className="overflow-hidden rounded-[var(--radius)] border">
              <table className="w-full text-sm">
                <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                  <tr>
                    <th className="px-4 py-2 text-left">Mã</th>
                    <th className="px-4 py-2 text-left">Tên</th>
                    <th className="px-4 py-2 text-left">Mô tả</th>
                    <th className="px-4 py-2 text-left">Trạng thái</th>
                  </tr>
                </thead>
                <tbody>
                  {records.map((c) => (
                    <tr key={c.id} className="border-t align-top">
                      <td className="px-4 py-3 font-mono text-xs">{c.code}</td>
                      <td className="px-4 py-3 font-medium">{c.name}</td>
                      <td className="px-4 py-3 text-[var(--color-muted-fg)]">{c.description ?? "—"}</td>
                      <td className="px-4 py-3">
                        {c.isActive ? (
                          <span className="text-emerald-700">hoạt động</span>
                        ) : (
                          <span className="text-red-700">ngừng</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ))
      )}
    </div>
  );
}
