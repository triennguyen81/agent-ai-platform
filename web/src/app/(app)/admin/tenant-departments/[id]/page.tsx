import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowLeft, Bot, Building2, Link2, Power, Puzzle, ShieldCheck, Users } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

interface DepartmentRecord { id: string; code: string; name: string; status: "active" | "inactive"; isDefault: boolean }
interface TenantAccount { id: string; email: string; displayName: string | null; isActive: boolean; departmentId?: string | null }
interface AssignedAgentRecord { agentId: string; slug: string; name: string; description: string | null; lifecycleState: string; enabled: boolean; assignedAt: string }
interface DepartmentAgentEntitlementRecord { agentId: string; assigned: boolean }
interface EligibleAgentRecord { agentId: string; slug: string; name: string }
interface DepartmentCapabilityRecord {
  capabilityId: string; code: string; name: string; category: string | null;
  boundAgentId: string | null; boundAgentName: string | null; eligibleAgents: EligibleAgentRecord[];
}

function errorRedirect(id: string, error: string): never {
  redirect(`/admin/tenant-departments/${id}?error=${encodeURIComponent(error.slice(0, 200))}`);
}

// ---- Server actions (TENANT_OWNER-only; every action re-derives the real
// role server-side via requireViewer, on top of the Gateway's own
// tenant_owner-literal policy on every one of these routes) --------------

async function renameAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const id = String(formData.get("departmentId") ?? "");
  const name = String(formData.get("name") ?? "").trim();
  if (!id || !name) errorRedirect(id, "ten-la-bat-buoc");
  const token = await requireSession();
  const res = await gateway.updateTenantDepartment(token, id, { name });
  if (!res.ok) errorRedirect(id, await res.text());
  revalidatePath(`/admin/tenant-departments/${id}`);
  redirect(`/admin/tenant-departments/${id}?ok=renamed`);
}

async function setStatusAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const id = String(formData.get("departmentId") ?? "");
  const action = String(formData.get("action") ?? ""); // "activate" | "deactivate"
  const token = await requireSession();
  const res = action === "deactivate"
    ? await gateway.deactivateTenantDepartment(token, id)
    : await gateway.activateTenantDepartment(token, id);
  if (!res.ok) errorRedirect(id, await res.text());
  revalidatePath(`/admin/tenant-departments/${id}`);
  redirect(`/admin/tenant-departments/${id}?ok=${action}d&tab=overview`);
}

async function moveOutAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const currentId = String(formData.get("currentDepartmentId") ?? "");
  const userId = String(formData.get("userId") ?? "");
  const destinationId = String(formData.get("destinationId") ?? "");
  if (!userId || !destinationId) errorRedirect(currentId, "yeu-cau-khong-hop-le");
  const token = await requireSession();
  const res = await gateway.changeTenantUserDepartment(token, userId, destinationId);
  if (!res.ok) errorRedirect(currentId, await res.text());
  revalidatePath(`/admin/tenant-departments/${currentId}`);
  redirect(`/admin/tenant-departments/${currentId}?ok=moved&tab=members`);
}

async function assignAgentAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const departmentId = String(formData.get("departmentId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  if (!agentId) errorRedirect(departmentId, "chon-agent");
  const token = await requireSession();
  const res = await gateway.assignDepartmentAgent(token, departmentId, agentId);
  if (!res.ok) errorRedirect(departmentId, await res.text());
  revalidatePath(`/admin/tenant-departments/${departmentId}`);
  redirect(`/admin/tenant-departments/${departmentId}?ok=agent-assigned&tab=agents`);
}

async function revokeAgentAction(formData: FormData): Promise<void> {
  "use server";
  await requireViewer(["tenant_owner"]);
  const departmentId = String(formData.get("departmentId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  const token = await requireSession();
  const res = await gateway.revokeDepartmentAgent(token, departmentId, agentId);
  if (!res.ok) errorRedirect(departmentId, "revoke-that-bai");
  revalidatePath(`/admin/tenant-departments/${departmentId}`);
  redirect(`/admin/tenant-departments/${departmentId}?ok=agent-revoked&tab=agents`);
}

export default async function TenantDepartmentDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string; tab?: string }>;
}) {
  await requireViewer(["tenant_owner"]);
  const { id } = await params;
  const { ok, error, tab } = await searchParams;
  const token = await requireSession();

  const [deptRes, allDeptsRes, usersRes, adminsRes, agentsRes, allAgentsRes, capabilitiesRes] = await Promise.all([
    gateway.getTenantDepartment(token, id),
    gateway.listTenantDepartments(token),
    gateway.listTenantUsers(token),
    gateway.listOwnTenantAdmins(token),
    gateway.listDepartmentAgents(token, id),
    gateway.listTenantAgents(token),
    gateway.listDepartmentCapabilities(token, id),
  ]);

  if (deptRes.status === 404) redirect("/admin/tenant-departments?error=khong-tim-thay-department");
  const department: DepartmentRecord = await deptRes.json();
  const allDepartments: DepartmentRecord[] = allDeptsRes.ok
    ? ((await allDeptsRes.json()) as { items: DepartmentRecord[] }).items
    : [];
  const otherDepartments = allDepartments.filter((d) => d.id !== id);

  const allUsers: TenantAccount[] = usersRes.ok ? ((await usersRes.json()) as { items: TenantAccount[] }).items : [];
  const members = allUsers.filter((u) => u.departmentId === id);
  const allAdmins: TenantAccount[] = adminsRes.ok ? ((await adminsRes.json()) as { items: TenantAccount[] }).items : [];
  const admins = allAdmins.filter((a) => a.departmentId === id);

  const agents: DepartmentAgentEntitlementRecord[] = agentsRes.ok
    ? ((await agentsRes.json()) as { items: DepartmentAgentEntitlementRecord[] }).items
    : [];
  const tenantAgents: AssignedAgentRecord[] = allAgentsRes.ok
    ? ((await allAgentsRes.json()) as { items: AssignedAgentRecord[] }).items
    : [];
  const agentsById = new Map(tenantAgents.map((a) => [a.agentId, a]));
  const assignedAgents = agents.filter((a) => a.assigned).map((a) => agentsById.get(a.agentId)).filter((a): a is AssignedAgentRecord => Boolean(a));
  const assignableAgents = tenantAgents.filter((a) => !agents.find((x) => x.agentId === a.agentId && x.assigned));

  const capabilities: DepartmentCapabilityRecord[] = capabilitiesRes.ok
    ? ((await capabilitiesRes.json()) as { items: DepartmentCapabilityRecord[] }).items
    : [];

  const TABS = [
    { value: "overview", label: "Overview" },
    { value: "members", label: `Members (${members.length})` },
    { value: "admins", label: `Admins (${admins.length})` },
    { value: "agents", label: `Agents (${assignedAgents.length})` },
    { value: "capabilities", label: `Nghiệp vụ (${capabilities.length})` },
  ] as const;
  const defaultTab = TABS.some((t) => t.value === tab) ? (tab as string) : "overview";

  return (
    <div className="space-y-6">
      <Link href="/admin/tenant-departments" className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]">
        <ArrowLeft className="size-3.5" /> Tất cả departments
      </Link>

      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">
            <Building2 className="mr-2 inline size-6" /> {department.name}
          </h1>
          {department.status === "active" ? (
            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">
              <ShieldCheck className="size-3" /> hoạt động
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700">
              <Power className="size-3" /> đã khoá
            </span>
          )}
          {department.isDefault && (
            <span className="rounded-full bg-[var(--color-muted)] px-2 py-0.5 text-xs text-[var(--color-muted-fg)]">mặc định — không thể khoá</span>
          )}
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{department.code}</p>
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">Thành công.</p>
      )}
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{decodeURIComponent(error)}</p>
      )}

      <Tabs defaultValue={defaultTab}>
        <TabsList>
          {TABS.map((t) => <TabsTrigger key={t.value} value={t.value}>{t.label}</TabsTrigger>)}
        </TabsList>

        <TabsContent value="overview" className="space-y-4">
          <section className="max-w-md space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <Label htmlFor="dept-name">Đổi tên department</Label>
            <form action={renameAction} className="flex gap-2">
              <input type="hidden" name="departmentId" value={department.id} />
              <Input id="dept-name" name="name" defaultValue={department.name} required />
              <Button type="submit" size="sm">Lưu</Button>
            </form>
          </section>
          <section className="max-w-md space-y-2 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <p className="text-sm text-[var(--color-muted-fg)]">
              {department.isDefault
                ? "Department mặc định của tenant không thể bị khoá."
                : department.status === "active"
                ? "Khoá department yêu cầu không còn thành viên, agent entitlement, hoặc nghiệp vụ nào đang hoạt động."
                : "Department đang bị khoá — mở lại để cho phép gán thành viên/agent mới."}
            </p>
            {!department.isDefault && (
              <form action={setStatusAction}>
                <input type="hidden" name="departmentId" value={department.id} />
                <input type="hidden" name="action" value={department.status === "active" ? "deactivate" : "activate"} />
                <Button type="submit" size="sm" variant={department.status === "active" ? "destructive" : "outline"}>
                  {department.status === "active" ? "Khoá department" : "Mở lại department"}
                </Button>
              </form>
            )}
          </section>
        </TabsContent>

        <TabsContent value="members" className="space-y-3">
          <MemberTable title="Members" icon={<Users className="mr-2 inline size-4" />} accounts={members} departmentId={id} otherDepartments={otherDepartments} />
        </TabsContent>

        <TabsContent value="admins" className="space-y-3">
          <MemberTable title="Admins" icon={<ShieldCheck className="mr-2 inline size-4" />} accounts={admins} departmentId={id} otherDepartments={otherDepartments} />
        </TabsContent>

        <TabsContent value="agents" className="space-y-3">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg"><Bot className="mr-2 inline size-4" /> Agents được gán ({assignedAgents.length})</h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              ADMIN của department này thực thi các agent dưới đây mà không cần gán trực tiếp cho từng user (D3).
            </p>
            <div className="overflow-hidden rounded border">
              <table className="w-full text-sm">
                <thead className="bg-[var(--color-muted)] text-xs uppercase text-[var(--color-muted-fg)]">
                  <tr><th className="px-3 py-2 text-left">Agent</th><th className="px-3 py-2 text-right">—</th></tr>
                </thead>
                <tbody>
                  {assignedAgents.length === 0 ? (
                    <tr><td colSpan={2} className="px-3 py-4 text-center text-[var(--color-muted-fg)]">Chưa gán agent nào.</td></tr>
                  ) : assignedAgents.map((a) => (
                    <tr key={a.agentId} className="border-t">
                      <td className="px-3 py-2">
                        <div className="font-medium">{a.name}</div>
                        <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{a.slug}</div>
                      </td>
                      <td className="px-3 py-2 text-right">
                        <form action={revokeAgentAction}>
                          <input type="hidden" name="departmentId" value={id} />
                          <input type="hidden" name="agentId" value={a.agentId} />
                          <Button type="submit" size="sm" variant="ghost">Revoke</Button>
                        </form>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {assignableAgents.length > 0 && (
              <form action={assignAgentAction} className="flex flex-wrap items-end gap-2 border-t pt-3">
                <input type="hidden" name="departmentId" value={id} />
                <div className="flex-1 space-y-1.5">
                  <Label htmlFor="agent-sel">Gán agent cho department</Label>
                  <select id="agent-sel" name="agentId" required className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm">
                    <option value="">— Chọn agent —</option>
                    {assignableAgents.map((a) => <option key={a.agentId} value={a.agentId}>{a.name}</option>)}
                  </select>
                </div>
                <Button type="submit" size="sm">Gán</Button>
              </form>
            )}
          </section>
        </TabsContent>

        <TabsContent value="capabilities" className="space-y-3">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg"><Puzzle className="mr-2 inline size-4" /> Nghiệp vụ ({capabilities.length})</h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Chỉ xem — từ ACL V2-D, Trưởng phòng (ADMIN) tự quản lý và gán nghiệp vụ của phòng ban mình
              qua Agent Builder; Tenant Owner giữ vai trò giám sát tổng quan, không còn chỉnh sửa gán ở đây.
            </p>
            {capabilities.length === 0 ? (
              <p className="text-sm text-[var(--color-muted-fg)]">
                Chưa có agent nào của department này công bố nghiệp vụ.
              </p>
            ) : (
              <div className="overflow-hidden rounded border">
                <table className="w-full text-sm">
                  <thead className="bg-[var(--color-muted)] text-xs uppercase text-[var(--color-muted-fg)]">
                    <tr><th className="px-3 py-2 text-left">Nghiệp vụ</th><th className="px-3 py-2 text-left">Agent phụ trách</th></tr>
                  </thead>
                  <tbody>
                    {capabilities.map((c) => (
                      <tr key={c.capabilityId} className="border-t">
                        <td className="px-3 py-2">
                          <div className="font-medium">{c.name}</div>
                          <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{c.code}</div>
                        </td>
                        <td className="px-3 py-2">
                          {c.boundAgentId ? (
                            <span className="inline-flex items-center gap-1 text-emerald-700"><Link2 className="size-3.5" /> {c.boundAgentName}</span>
                          ) : c.eligibleAgents.length === 1 ? (
                            <span className="text-[var(--color-muted-fg)]">Tự động: {c.eligibleAgents[0].name}</span>
                          ) : c.eligibleAgents.length === 0 ? (
                            <span className="text-[var(--color-muted-fg)]">Chưa có agent phù hợp</span>
                          ) : (
                            <span className="text-amber-700">Chưa gán — {c.eligibleAgents.length} agent phù hợp</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </TabsContent>
      </Tabs>
    </div>
  );
}

function MemberTable({
  title, icon, accounts, departmentId, otherDepartments,
}: {
  title: string;
  icon: React.ReactNode;
  accounts: TenantAccount[];
  departmentId: string;
  otherDepartments: DepartmentRecord[];
}) {
  return (
    <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
      <h2 className="font-serif text-lg">{icon}{title} ({accounts.length})</h2>
      <div className="overflow-hidden rounded border">
        <table className="w-full text-sm">
          <thead className="bg-[var(--color-muted)] text-xs uppercase text-[var(--color-muted-fg)]">
            <tr><th className="px-3 py-2 text-left">Email</th><th className="px-3 py-2 text-left">Trạng thái</th><th className="px-3 py-2 text-right">—</th></tr>
          </thead>
          <tbody>
            {accounts.length === 0 ? (
              <tr><td colSpan={3} className="px-3 py-4 text-center text-[var(--color-muted-fg)]">Chưa có ai.</td></tr>
            ) : accounts.map((a) => (
              <tr key={a.id} className="border-t">
                <td className="px-3 py-2">
                  <div className="font-medium">{a.email}</div>
                  {a.displayName && <div className="text-[10px] text-[var(--color-muted-fg)]">{a.displayName}</div>}
                </td>
                <td className="px-3 py-2">
                  {a.isActive ? <span className="text-emerald-700">hoạt động</span> : <span className="text-red-700">đã khoá</span>}
                </td>
                <td className="px-3 py-2 text-right">
                  {otherDepartments.length > 0 && (
                    <form action={moveOutAction} className="flex justify-end gap-1.5">
                      <input type="hidden" name="currentDepartmentId" value={departmentId} />
                      <input type="hidden" name="userId" value={a.id} />
                      <select name="destinationId" required defaultValue="" className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-xs">
                        <option value="" disabled>Chuyển đến...</option>
                        {otherDepartments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                      </select>
                      <Button type="submit" size="sm" variant="outline">Chuyển</Button>
                    </form>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
