import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { CheckCircle2, Link2, Plus, Power, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";

// ACL V2-D: ADMIN's own department Business Capability ("Nghiệp vụ")
// ownership — CRUD for capabilities this department created, plus
// operational binding (which of the department's eligible agents actually
// handles each capability). Tenant/department are always derived
// server-side from the caller's session — never sent from this page.

interface CapabilityRecord {
  id: string;
  code: string;
  name: string;
  description: string | null;
  category: string | null;
  isActive: boolean;
  scope: "platform" | "department";
}

interface EligibleAgent {
  agentId: string;
  slug: string;
  name: string;
}

interface DepartmentCapabilityBindingRecord {
  capabilityId: string;
  code: string;
  name: string;
  category: string | null;
  boundAgentId: string | null;
  boundAgentName: string | null;
  eligibleAgents: EligibleAgent[];
}

function errorRedirect(message: string): never {
  redirect(`/department/capabilities?error=${encodeURIComponent(message.slice(0, 200))}`);
}

async function createCapabilityAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["admin"]);
  const code = String(formData.get("code") ?? "").trim();
  const name = String(formData.get("name") ?? "").trim();
  const category = String(formData.get("category") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  if (!code || !name) errorRedirect("code-and-name-required");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentCapability(viewer.token, {
    code,
    name,
    category: category || undefined,
    description: description || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    errorRedirect(body || `HTTP ${res.status}`);
  }
  revalidatePath("/department/capabilities");
  redirect("/department/capabilities?ok=created");
}

async function toggleEnabledAction(formData: FormData): Promise<void> {
  "use server";
  const capabilityId = String(formData.get("capabilityId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!capabilityId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res =
    action === "enable"
      ? await gateway.enableDepartmentCapability(viewer.token, capabilityId)
      : await gateway.disableDepartmentCapability(viewer.token, capabilityId);
  if (!res.ok) errorRedirect("toggle-failed");
  revalidatePath("/department/capabilities");
  redirect(`/department/capabilities?ok=${action}d`);
}

async function setBindingAction(formData: FormData): Promise<void> {
  "use server";
  const capabilityId = String(formData.get("capabilityId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  if (!capabilityId || !agentId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.setOwnDepartmentCapabilityBinding(viewer.token, capabilityId, agentId);
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/capabilities");
  redirect("/department/capabilities?ok=binding-set");
}

async function revokeBindingAction(formData: FormData): Promise<void> {
  "use server";
  const capabilityId = String(formData.get("capabilityId") ?? "");
  if (!capabilityId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.revokeOwnDepartmentCapabilityBinding(viewer.token, capabilityId);
  if (!res.ok) errorRedirect("revoke-failed");
  revalidatePath("/department/capabilities");
  redirect("/department/capabilities?ok=binding-revoked");
}

export default async function DepartmentCapabilitiesPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const viewer = await requireViewer(["admin"]);

  const [ownRes, bindingsRes] = await Promise.all([
    gateway.listOwnDepartmentCapabilities(viewer.token),
    gateway.listOwnDepartmentCapabilityBindings(viewer.token),
  ]);
  const ownCapabilities: CapabilityRecord[] = ownRes.ok
    ? ((await ownRes.json()) as { items: CapabilityRecord[] }).items
    : [];
  const bindings: DepartmentCapabilityBindingRecord[] = bindingsRes.ok
    ? ((await bindingsRes.json()) as { items: DepartmentCapabilityBindingRecord[] }).items
    : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Nghiệp vụ</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Một danh sách hợp nhất gồm nghiệp vụ Hệ thống và nghiệp vụ của phòng ban. Nghiệp vụ Hệ thống
          chỉ được xem, dùng và gắn; chỉ nghiệp vụ Phòng ban mới có thể chỉnh sửa trạng thái.
        </p>
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
          <CheckCircle2 className="mr-1 inline size-4" /> Thành công.
        </p>
      )}
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {decodeURIComponent(error)}
        </p>
      )}

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="flex items-center gap-2 font-serif text-lg">
          <Plus className="size-4" /> Tạo nghiệp vụ mới
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Mã nghiệp vụ (code) phải là duy nhất trên toàn hệ thống — dùng dạng{" "}
          <code className="font-mono">domain.action</code> (VD: <code className="font-mono">crm.lead_management</code>).
        </p>
        <form action={createCapabilityAction} className="grid gap-3 sm:grid-cols-4">
          <div className="space-y-1.5">
            <Label htmlFor="c-code">Mã (code)</Label>
            <Input id="c-code" name="code" required placeholder="crm.lead_management" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="c-name">Tên</Label>
            <Input id="c-name" name="name" required placeholder="Quản lý khách hàng tiềm năng" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="c-category">Danh mục (tuỳ chọn)</Label>
            <Input id="c-category" name="category" placeholder="crm" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="c-desc">Mô tả (tuỳ chọn)</Label>
            <Input id="c-desc" name="description" />
          </div>
          <div className="sm:col-span-4">
            <Button type="submit" size="sm">Tạo</Button>
          </div>
        </form>
      </section>

      <section className="space-y-3">
        <h2 className="font-serif text-lg">Nghiệp vụ khả dụng ({ownCapabilities.length})</h2>
        {ownCapabilities.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Phòng ban bạn chưa tạo nghiệp vụ riêng nào. Bạn vẫn có thể dùng nghiệp vụ nền tảng cho Agent của mình.
          </div>
        ) : (
          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-4 py-2 text-left">Nghiệp vụ</th>
                  <th className="px-4 py-2 text-left">Danh mục</th>
                  <th className="px-4 py-2 text-left">Trạng thái</th>
                  <th className="px-4 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {ownCapabilities.map((c) => (
                  <tr key={c.id} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{c.name}</div>
                      <div className="mt-1"><ResourceScopeBadge scope={c.scope} /></div>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{c.code}</div>
                    </td>
                    <td className="px-4 py-3 text-xs text-[var(--color-muted-fg)]">{c.category ?? "—"}</td>
                    <td className="px-4 py-3">
                      {c.isActive ? (
                        <span className="inline-flex items-center gap-1 text-emerald-700">
                          <ShieldCheck className="size-3" /> hoạt động
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 text-red-700">
                          <Power className="size-3" /> tắt
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      {c.scope === "department" ? <form action={toggleEnabledAction}>
                        <input type="hidden" name="capabilityId" value={c.id} />
                        <input type="hidden" name="action" value={c.isActive ? "disable" : "enable"} />
                        <Button type="submit" size="sm" variant="outline">
                          {c.isActive ? "Disable" : "Enable"}
                        </Button>
                      </form> : <span className="text-xs text-[var(--color-muted-fg)]">Chỉ đọc</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="font-serif text-lg">Gán Agent phụ trách ({bindings.length})</h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Với mỗi nghiệp vụ agent của phòng ban đang công bố, chỉ định rõ agent nào phụ trách nếu có
          nhiều hơn một agent phù hợp. Nếu chỉ có đúng một agent phù hợp, hệ thống tự động dùng agent đó.
        </p>
        {bindings.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Chưa có Agent nào của phòng ban công bố nghiệp vụ.
          </div>
        ) : (
          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-4 py-2 text-left">Nghiệp vụ</th>
                  <th className="px-4 py-2 text-left">Agent phụ trách</th>
                  <th className="px-4 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {bindings.map((b) => (
                  <tr key={b.capabilityId} className="border-t align-top">
                    <td className="px-4 py-3">
                      <div className="font-medium">{b.name}</div>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{b.code}</div>
                    </td>
                    <td className="px-4 py-3">
                      {b.boundAgentId ? (
                        <span className="inline-flex items-center gap-1 text-emerald-700">
                          <Link2 className="size-3.5" /> {b.boundAgentName}
                        </span>
                      ) : b.eligibleAgents.length === 1 ? (
                        <span className="text-[var(--color-muted-fg)]">Tự động: {b.eligibleAgents[0].name}</span>
                      ) : (
                        <span className="text-amber-700">Chưa gán — {b.eligibleAgents.length} agent phù hợp</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <div className="flex justify-end gap-2">
                        {b.eligibleAgents.length > 0 && (
                          <form action={setBindingAction} className="flex items-center gap-1.5">
                            <input type="hidden" name="capabilityId" value={b.capabilityId} />
                            <select
                              name="agentId"
                              required
                              defaultValue={b.boundAgentId ?? ""}
                              className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-xs"
                            >
                              <option value="" disabled>— Chọn agent —</option>
                              {b.eligibleAgents.map((a) => (
                                <option key={a.agentId} value={a.agentId}>{a.name}</option>
                              ))}
                            </select>
                            <Button type="submit" size="sm" variant="outline">{b.boundAgentId ? "Đổi" : "Gán"}</Button>
                          </form>
                        )}
                        {b.boundAgentId && (
                          <form action={revokeBindingAction}>
                            <input type="hidden" name="capabilityId" value={b.capabilityId} />
                            <Button type="submit" size="sm" variant="ghost">Gỡ</Button>
                          </form>
                        )}
                      </div>
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
