import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { ArrowLeft, Bot } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";

interface TenantUser { id: string; email: string; displayName: string | null; isActive: boolean }
interface TenantAgent { agentId: string; name: string; slug: string; enabled: boolean; lifecycleState: string }
interface Assignment { userId: string }

async function assignmentAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!userId || !agentId || !["assign", "revoke"].includes(action)) redirect("/admin/tenant-users?error=invalid-input");
  const token = await requireSession();
  const response = action === "assign"
    ? await gateway.assignTenantAgentUser(token, agentId, userId)
    : await gateway.revokeTenantAgentUser(token, agentId, userId);
  if (!response.ok) redirect(`/admin/tenant-users/${userId}/agents?error=${action}-failed-${response.status}`);
  revalidatePath(`/admin/tenant-users/${userId}/agents`);
  revalidatePath("/my-agents");
  redirect(`/admin/tenant-users/${userId}/agents?ok=${action}ed`);
}

export default async function UserAgentAssignmentsPage({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const viewer = await requireViewer(["tenant_owner", "admin"]);
  const { id } = await params;
  const { ok, error } = await searchParams;
  const [usersResponse, agentsResponse] = await Promise.all([
    gateway.listTenantUsers(viewer.token),
    gateway.listTenantAgents(viewer.token),
  ]);
  const users: TenantUser[] = usersResponse.ok ? ((await usersResponse.json()) as { items: TenantUser[] }).items : [];
  const user = users.find((candidate) => candidate.id === id);
  if (!user) redirect("/admin/tenant-users?error=user-not-found");
  const agents: TenantAgent[] = agentsResponse.ok ? ((await agentsResponse.json()) as { items: TenantAgent[] }).items : [];
  const assignmentResponses = await Promise.all(agents.map((agent) => gateway.listTenantAgentUsers(viewer.token, agent.agentId)));
  const assigned = new Set<string>();
  for (let index = 0; index < agents.length; index += 1) {
    const response = assignmentResponses[index];
    if (response.ok) {
      const rows = ((await response.json()) as { items: Assignment[] }).items;
      if (rows.some((row) => row.userId === id)) assigned.add(agents[index].agentId);
    }
  }

  return (
    <div className="space-y-6">
      <Link href="/admin/tenant-users" className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"><ArrowLeft className="size-4" />Users</Link>
      <header><h1 className="font-serif text-3xl">Agent assignments</h1><p className="text-sm text-[var(--color-muted-fg)]">{user.email} · quyền thực thi trong tenant đã xác thực</p></header>
      {ok && <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">Đã cập nhật assignment.</p>}
      {error && <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{decodeURIComponent(error)}</p>}
      {!user.isActive && <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">User đang bị khoá; runtime sẽ từ chối thực thi dù assignment còn tồn tại.</p>}
      <div className="grid gap-3 sm:grid-cols-2">
        {agents.map((agent) => {
          const isAssigned = assigned.has(agent.agentId);
          return <article key={agent.agentId} className="flex items-center justify-between rounded-[var(--radius)] border p-4">
            <div><div className="flex items-center gap-2 font-medium"><Bot className="size-4" />{agent.name}</div><div className="text-xs text-[var(--color-muted-fg)]">{agent.slug} · {agent.lifecycleState}{!agent.enabled && " · disabled"}</div></div>
            <form action={assignmentAction}><input type="hidden" name="userId" value={id} /><input type="hidden" name="agentId" value={agent.agentId} /><input type="hidden" name="action" value={isAssigned ? "revoke" : "assign"} /><Button type="submit" size="sm" variant={isAssigned ? "ghost" : "outline"} disabled={!user.isActive && !isAssigned}>{isAssigned ? "Revoke" : "Assign"}</Button></form>
          </article>;
        })}
        {agents.length === 0 && <p className="text-sm text-[var(--color-muted-fg)]">Tenant chưa được gán agent nào.</p>}
      </div>
    </div>
  );
}
