import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { Building2, ChevronRight, Plus } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface DepartmentRecord {
  id: string;
  code: string;
  name: string;
  status: "active" | "inactive";
  isDefault: boolean;
}

// D5 §3: TENANT_OWNER creates a department in their own tenant only —
// tenant_id is always derived server-side by the Gateway from the session,
// never accepted here. is_default is never settable by this form.
async function createDepartmentAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const code = String(formData.get("code") ?? "").trim().toLowerCase();
  const name = String(formData.get("name") ?? "").trim();
  if (!code || !name) redirect("/admin/tenant-departments?error=ma-va-ten-la-bat-buoc");

  const token = await requireSession();
  const res = await gateway.createTenantDepartment(token, { code, name });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-departments?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath("/admin/tenant-departments");
  redirect("/admin/tenant-departments?ok=created");
}

export default async function TenantDepartmentsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  await requireViewer(["tenant_owner"]);
  const { ok, error } = await searchParams;
  const token = await requireSession();

  const res = await gateway.listTenantDepartments(token);
  const departments: DepartmentRecord[] = res.ok
    ? ((await res.json()) as { items: DepartmentRecord[] }).items
    : [];

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header className="space-y-1">
          <h1 className="font-serif text-3xl">
            <Building2 className="mr-2 inline size-6" /> Departments
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Quản lý department trong tenant của bạn. Mở một department để xem thành viên, admin, agent
            và nghiệp vụ đã gán.
          </p>
        </header>

        {ok && (
          <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            {ok === "created" ? "Đã tạo department." : "Thành công."}
          </p>
        )}
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {decodeURIComponent(error)}
          </p>
        )}

        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]">
              <tr>
                <th className="px-4 py-2 text-left">Tên</th>
                <th className="px-4 py-2 text-left">Mã</th>
                <th className="px-4 py-2 text-left">Trạng thái</th>
                <th className="px-4 py-2 text-right"></th>
              </tr>
            </thead>
            <tbody>
              {departments.map((d) => (
                <tr key={d.id} className="border-t">
                  <td className="px-4 py-3 font-medium">
                    {d.name} {d.isDefault && <span className="ml-1 rounded-full bg-[var(--color-muted)] px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)]">mặc định</span>}
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-[var(--color-muted-fg)]">{d.code}</td>
                  <td className="px-4 py-3">
                    {d.status === "active" ? (
                      <span className="text-emerald-700">hoạt động</span>
                    ) : (
                      <span className="text-red-700">đã khoá</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Link
                      href={`/admin/tenant-departments/${d.id}`}
                      className="inline-flex items-center gap-1 text-[var(--color-primary)] hover:underline"
                    >
                      Chi tiết <ChevronRight className="size-3.5" />
                    </Link>
                  </td>
                </tr>
              ))}
              {departments.length === 0 && (
                <tr>
                  <td colSpan={4} className="p-8 text-center text-[var(--color-muted-fg)]">
                    Tenant chưa có department nào.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="space-y-4">
        <h2 className="font-serif text-xl">
          <Plus className="mr-1 inline size-4" /> Tạo department
        </h2>
        <form action={createDepartmentAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="d-code">Mã (duy nhất trong tenant)</Label>
            <Input id="d-code" name="code" required placeholder="support" pattern="[a-z0-9-]+" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="d-name">Tên</Label>
            <Input id="d-name" name="name" required placeholder="Support" />
          </div>
          <Button type="submit" className="w-full">Tạo department</Button>
        </form>
      </aside>
    </div>
  );
}
