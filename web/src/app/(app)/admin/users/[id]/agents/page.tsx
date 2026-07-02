import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { AlertCircle, ArrowLeft, ShieldCheck, UserCog } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/input";

interface AgentRecord {
  id: string;
  tenantId: string;
  code: string;
  name: string;
  type: string;
  lifecycleState: string;
  actionTier: string;
  disabledAt: string | null;
}

interface UserAgent {
  agentId: string;
  agentCode: string;
  agentName: string;
  agentType: string;
  lifecycleState: string;
  actionTier: string;
  agentDisabled: boolean;
  accessLevel: string;
}

interface UserWithRoles {
  id: string;
  email: string;
  displayName: string | null;
  roles: string[];
}

async function grantAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  const accessLevel = String(formData.get("accessLevel") ?? "user");
  if (!userId || !agentId) redirect(`/admin/users/${userId}/agents?error=invalid`);

  const token = await requireSession();
  const res = await gateway.grantAgentAcl(token, agentId, {
    principalId: userId,
    accessLevel,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/users/${userId}/agents?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/users/${userId}/agents`);
  redirect(`/admin/users/${userId}/agents?ok=granted`);
}

async function revokeAction(formData: FormData): Promise<void> {
  "use server";
  const userId = String(formData.get("userId") ?? "");
  const agentId = String(formData.get("agentId") ?? "");
  if (!userId || !agentId) redirect(`/admin/users/${userId}/agents?error=invalid`);

  const token = await requireSession();
  const res = await gateway.revokeAgentAcl(token, agentId, userId);
  if (!res.ok) {
    redirect(`/admin/users/${userId}/agents?error=revoke-failed`);
  }
  revalidatePath(`/admin/users/${userId}/agents`);
  redirect(`/admin/users/${userId}/agents?ok=revoked`);
}

const LIFECYCLE_BADGE: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  testing: "border-blue-300 bg-blue-50 text-blue-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
  deprecated: "border-orange-300 bg-orange-50 text-orange-700",
  retired: "border-gray-300 bg-gray-100 text-gray-500",
};

const ACCESS_BADGE: Record<string, string> = {
  owner: "bg-red-600 text-white",
  editor: "bg-amber-500 text-white",
  user: "bg-emerald-600 text-white",
};

export default async function AssignAgentsPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { id: userId } = await params;
  const { ok, error } = await searchParams;

  const token = await requireSession();
  const [usersRes, agentsRes, userAgentsRes] = await Promise.all([
    gateway.listUsers(token),
    gateway.listAgents(token),
    gateway.listAgentsForUser(token, userId),
  ]);

  if (usersRes.status === 403 || agentsRes.status === 403) redirect("/chat");

  const users = usersRes.ok
    ? ((await usersRes.json()) as { items: UserWithRoles[] }).items
    : [];
  const targetUser = users.find((u) => u.id === userId) ?? null;

  const allAgents: AgentRecord[] = agentsRes.ok
    ? ((await agentsRes.json()) as { items: AgentRecord[] }).items
    : [];
  const assigned: UserAgent[] = userAgentsRes.ok
    ? ((await userAgentsRes.json()) as { items: UserAgent[] }).items
    : [];
  const assignedIds = new Set(assigned.map((a) => a.agentId));

  // Split for clarity
  const grantable = allAgents.filter((a) => !assignedIds.has(a.id));

  return (
    <div className="space-y-6">
      <Link
        href="/admin/users"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Tất cả users
      </Link>

      <header className="space-y-1">
        <h1 className="font-serif text-3xl">
          <UserCog className="mr-2 inline size-6" /> Gán agent cho user
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          {targetUser ? (
            <>
              Đang quản lý: <strong>{targetUser.email}</strong>{" "}
              {targetUser.displayName && <>({targetUser.displayName})</>}{" "}
              · Roles: {targetUser.roles.join(", ")}
            </>
          ) : (
            "User không tồn tại"
          )}
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

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">Agent đã gán ({assigned.length})</h2>
        {assigned.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">
            User này chưa được gán agent nào — hiện tại không thể dùng agent nào cả.
          </p>
        ) : (
          <div className="overflow-hidden rounded border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-3 py-2 text-left">Agent</th>
                  <th className="px-3 py-2 text-left">Lifecycle</th>
                  <th className="px-3 py-2 text-left">Tier</th>
                  <th className="px-3 py-2 text-left">Access level</th>
                  <th className="px-3 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {assigned.map((a) => (
                  <tr key={a.agentId} className="border-t align-top">
                    <td className="px-3 py-2">
                      <Link href={`/admin/agents/${a.agentId}`} className="font-medium hover:underline">
                        {a.agentName}
                      </Link>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                        {a.agentCode}
                      </div>
                      {a.agentDisabled && (
                        <div className="mt-0.5 inline-flex items-center gap-1 text-[10px] text-red-700">
                          <AlertCircle className="size-3" /> Agent đã bị disable
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <span
                        className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
                          LIFECYCLE_BADGE[a.lifecycleState] ?? "border-gray-300"
                        }`}
                      >
                        {a.lifecycleState === "production" && <ShieldCheck className="size-3" />}
                        {a.lifecycleState}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-xs">{a.actionTier}</td>
                    <td className="px-3 py-2">
                      <span
                        className={`rounded-full px-2 py-0.5 text-xs ${
                          ACCESS_BADGE[a.accessLevel] ?? "bg-gray-500 text-white"
                        }`}
                      >
                        {a.accessLevel}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-right">
                      <form action={revokeAction}>
                        <input type="hidden" name="userId" value={userId} />
                        <input type="hidden" name="agentId" value={a.agentId} />
                        <Button type="submit" size="sm" variant="ghost">
                          Gỡ quyền
                        </Button>
                      </form>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">Gán agent mới</h2>
        {grantable.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">
            User đã có tất cả agent trong tenant.
          </p>
        ) : (
          <form action={grantAction} className="flex flex-wrap items-end gap-2">
            <input type="hidden" name="userId" value={userId} />
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="agent-sel">Agent</Label>
              <select
                id="agent-sel"
                name="agentId"
                required
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              >
                <option value="">— Chọn agent —</option>
                {grantable.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name} ({a.code}) · {a.lifecycleState} · {a.actionTier}
                  </option>
                ))}
              </select>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="lvl-sel">Access level</Label>
              <select
                id="lvl-sel"
                name="accessLevel"
                className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                defaultValue="user"
              >
                <option value="user">user (chỉ dùng)</option>
                <option value="editor">editor (sửa config)</option>
                <option value="owner">owner (toàn quyền)</option>
              </select>
            </div>
            <Button type="submit" size="sm">
              Gán
            </Button>
          </form>
        )}
      </section>
    </div>
  );
}
