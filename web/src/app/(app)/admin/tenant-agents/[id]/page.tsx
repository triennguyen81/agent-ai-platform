import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowLeft, ShieldCheck, UserCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/input";

interface TenantAssignedAgentRecord {
  agentId: string;
  slug: string;
  name: string;
  description: string | null;
  lifecycleState: string;
  enabled: boolean;
  assignedAt: string;
}

interface AgentUserAssignmentRecord {
  userId: string;
  email: string;
  displayName: string | null;
  assignedAt: string;
}

interface TenantUserRecord {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
}
interface KnowledgeRecord { id: string; noteId: string; title: string; updatedAt?: string }

function formatDateTime(s: string): string {
  try {
    return new Date(s).toLocaleString("vi-VN");
  } catch {
    return s;
  }
}

async function assignUserAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const userId = String(formData.get("userId") ?? "").trim();
  if (!agentId || !userId) redirect(`/admin/tenant-agents/${agentId}?error=invalid`);

  // No tenantId is ever read from the form — the Gateway derives it from
  // this admin's own session, so a forged tenantId in the request has no path in.
  const token = await requireSession();
  const res = await gateway.assignTenantAgentUser(token, agentId, userId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/tenant-agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/tenant-agents/${agentId}`);
  redirect(`/admin/tenant-agents/${agentId}?ok=user-assigned`);
}

async function revokeUserAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const userId = String(formData.get("userId") ?? "");
  if (!agentId || !userId) redirect("/admin/tenant-agents");

  const token = await requireSession();
  const res = await gateway.revokeTenantAgentUser(token, agentId, userId);
  if (!res.ok) redirect(`/admin/tenant-agents/${agentId}?error=revoke-failed`);
  revalidatePath(`/admin/tenant-agents/${agentId}`);
  redirect(`/admin/tenant-agents/${agentId}?ok=user-revoked`);
}
async function knowledgeAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? ""); const noteId = String(formData.get("noteId") ?? ""); const action = String(formData.get("action") ?? "");
  if (!agentId || !noteId) redirect("/admin/tenant-agents");
  const token = await requireSession();
  const response = action === "detach" ? await gateway.detachTenantAgentKnowledge(token, agentId, noteId) : await gateway.attachTenantAgentKnowledge(token, agentId, noteId);
  if (!response.ok) redirect(`/admin/tenant-agents/${agentId}?error=knowledge-${response.status}`);
  revalidatePath(`/admin/tenant-agents/${agentId}`); redirect(`/admin/tenant-agents/${agentId}?ok=knowledge-${action}`);
}

export default async function TenantAgentDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { id } = await params;
  const { ok, error } = await searchParams;
  const token = await requireSession();

  const [agentsRes, usersRes, assignedRes, knowledgeRes, attachableRes] = await Promise.all([
    gateway.listTenantAgents(token),
    gateway.listTenantUsers(token),
    gateway.listTenantAgentUsers(token, id),
    gateway.listTenantAgentKnowledge(token, id),
    gateway.listAttachableTenantAgentKnowledge(token, id),
  ]);

  if (agentsRes.status === 400 || agentsRes.status === 403) redirect("/chat");
  const agents: TenantAssignedAgentRecord[] = agentsRes.ok
    ? ((await agentsRes.json()) as { items: TenantAssignedAgentRecord[] }).items
    : [];
  const thisAgent = agents.find((a) => a.agentId === id);

  // Not in "my tenant's assigned agents" list: either it doesn't exist, or
  // (more likely) it isn't assigned to this admin's tenant — either way, no
  // access here. The Gateway itself independently enforces this on every
  // assign/revoke/list call below via agent_tenant_assignments, this is just
  // the page-level reflection of that so the admin sees a clear message.
  if (!thisAgent || assignedRes.status === 404) {
    return (
      <div className="space-y-4">
        <Link
          href="/admin/tenant-agents"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Agents của tenant
        </Link>
        <p className="text-sm text-red-700">
          Agent này chưa được assign cho tenant của bạn (hoặc không tồn tại).
        </p>
      </div>
    );
  }

  const users: TenantUserRecord[] = usersRes.ok
    ? ((await usersRes.json()) as { items: TenantUserRecord[] }).items
    : [];
  const assignedUsers: AgentUserAssignmentRecord[] = assignedRes.ok
    ? ((await assignedRes.json()) as { items: AgentUserAssignmentRecord[] }).items
    : [];
  const assignedUserIds = new Set(assignedUsers.map((u) => u.userId));
  const assignableUsers = users.filter((u) => !assignedUserIds.has(u.id));
  const knowledge: KnowledgeRecord[] = knowledgeRes.ok ? ((await knowledgeRes.json()) as { items: Omit<KnowledgeRecord, "noteId">[] }).items.map((note) => ({ ...note, noteId: note.id })) : [];
  const attachable: KnowledgeRecord[] = attachableRes.ok ? ((await attachableRes.json()) as { items: Omit<KnowledgeRecord, "noteId">[] }).items.map((note) => ({ ...note, noteId: note.id })) : [];

  return (
    <div className="space-y-6">
      <Link
        href="/admin/tenant-agents"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Agents của tenant
      </Link>

      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{thisAgent.name}</h1>
          {thisAgent.lifecycleState === "production" && thisAgent.enabled && (
            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">
              <ShieldCheck className="size-3" /> production
            </span>
          )}
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{thisAgent.slug}</p>
        {thisAgent.description && <p className="text-sm">{thisAgent.description}</p>}
        {!thisAgent.enabled && (
          <p className="text-xs text-red-700">
            SUPER_ADMIN đã disable agent này — user được gán vẫn sẽ bị chặn thực thi.
          </p>
        )}
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

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">
          <UserCheck className="mr-2 inline size-4" /> Users được gán ({assignedUsers.length})
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Chỉ user trong tenant của bạn mới xuất hiện ở đây. Đây là quyền thực thi (execution
          entitlement) — không phải ACL, sẽ có hiệu lực ngay lập tức trên trang &quot;Trợ lý AI của tôi&quot; của user.
        </p>

        <div className="overflow-hidden rounded border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">User</th>
                <th className="px-3 py-2 text-left">Gán lúc</th>
                <th className="px-3 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {assignedUsers.length === 0 ? (
                <tr>
                  <td colSpan={3} className="px-3 py-4 text-center text-[var(--color-muted-fg)]">
                    Chưa gán user nào.
                  </td>
                </tr>
              ) : (
                assignedUsers.map((u) => (
                  <tr key={u.userId} className="border-t">
                    <td className="px-3 py-2">
                      <div className="font-medium">{u.email}</div>
                      {u.displayName && (
                        <div className="text-[10px] text-[var(--color-muted-fg)]">{u.displayName}</div>
                      )}
                    </td>
                    <td className="px-3 py-2 text-xs text-[var(--color-muted-fg)]">
                      {formatDateTime(u.assignedAt)}
                    </td>
                    <td className="px-3 py-2 text-right">
                      <form action={revokeUserAction}>
                        <input type="hidden" name="agentId" value={thisAgent.agentId} />
                        <input type="hidden" name="userId" value={u.userId} />
                        <Button type="submit" size="sm" variant="ghost">
                          Revoke
                        </Button>
                      </form>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {assignableUsers.length > 0 ? (
          <form action={assignUserAction} className="flex flex-wrap items-end gap-2 border-t pt-3">
            <input type="hidden" name="agentId" value={thisAgent.agentId} />
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="u-sel">Gán agent cho user</Label>
              <select
                id="u-sel"
                name="userId"
                required
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              >
                <option value="">— Chọn user —</option>
                {assignableUsers.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email}
                    {u.displayName && ` (${u.displayName})`}
                  </option>
                ))}
              </select>
            </div>
            <Button type="submit" size="sm">
              Gán
            </Button>
          </form>
        ) : (
          <p className="text-xs text-[var(--color-muted-fg)]">
            Mọi user trong tenant đều đã được gán agent này (hoặc tenant chưa có user nào).
          </p>
        )}
      </section>
      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5"><h2 className="font-serif text-lg">Tenant knowledge ({knowledge.length})</h2><p className="text-xs text-[var(--color-muted-fg)]">Gateway derives the tenant from your session. Notes from other tenants cannot appear or be attached.</p><div className="space-y-2">{knowledge.map((note) => <div key={note.noteId} className="flex items-center justify-between rounded border px-3 py-2 text-sm"><span>{note.title}</span><form action={knowledgeAction}><input type="hidden" name="agentId" value={id}/><input type="hidden" name="noteId" value={note.noteId}/><input type="hidden" name="action" value="detach"/><Button type="submit" size="sm" variant="ghost">Detach</Button></form></div>)}{knowledge.length === 0 && <p className="text-sm text-[var(--color-muted-fg)]">No attached knowledge.</p>}</div>{attachable.length > 0 && <form action={knowledgeAction} className="flex gap-2 border-t pt-3"><input type="hidden" name="agentId" value={id}/><input type="hidden" name="action" value="attach"/><select name="noteId" required className="flex-1 rounded border bg-[var(--color-bg)] px-2 text-sm"><option value="">Select tenant note</option>{attachable.map((note) => <option key={note.noteId} value={note.noteId}>{note.title}</option>)}</select><Button type="submit" size="sm">Attach</Button></form>}</section>
    </div>
  );
}
