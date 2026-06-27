import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { AlertTriangle, ArrowLeft, History, Power, ShieldCheck, UserCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

interface AgentRecord {
  id: string;
  tenantId: string;
  code: string;
  name: string;
  type: string;
  description: string | null;
  lifecycleState: string;
  ownerUserId: string | null;
  sponsorTeam: string | null;
  actionTier: string;
  configJson: string;
  disabledAt: string | null;
  disabledBy: string | null;
  disabledReason: string | null;
  createdAt: string;
  updatedAt: string;
}

interface AclEntry {
  agentId: string;
  principalId: string;
  principalType: string;
  accessLevel: string;
  grantedAt: string;
}

interface LifecycleEvent {
  id: string;
  agentId: string;
  fromState: string | null;
  toState: string;
  transitionedBy: string | null;
  reason: string | null;
  occurredAt: string;
}

// Same map as list page — keep these in sync.
const LIFECYCLE_BADGES: Record<string, string> = {
  draft: "border-gray-300 bg-gray-50 text-gray-700",
  testing: "border-blue-300 bg-blue-50 text-blue-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-700",
  production: "border-emerald-300 bg-emerald-50 text-emerald-700",
  deprecated: "border-orange-300 bg-orange-50 text-orange-700",
  retired: "border-gray-300 bg-gray-100 text-gray-500",
};

// Allowed forward transitions per state. Mirrors AgentRepository state machine.
const ALLOWED_NEXT: Record<string, string[]> = {
  draft: ["testing", "retired"],
  testing: ["pending_review", "draft", "retired"],
  pending_review: ["production", "testing", "retired"],
  production: ["deprecated"],
  deprecated: ["retired", "production"],
  retired: [],
};

async function transitionAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const toState = String(formData.get("toState") ?? "");
  const reason = String(formData.get("reason") ?? "");
  if (!agentId || !toState) redirect(`/admin/agents/${agentId}?error=invalid`);

  const token = await requireSession();
  const res = await gateway.transitionAgent(token, agentId, { toState, reason: reason || undefined });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=transitioned`);
}

async function killSwitchAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const action = String(formData.get("action") ?? "");
  const reason = String(formData.get("reason") ?? "");
  if (!agentId) redirect("/admin/agents");

  const token = await requireSession();
  const res =
    action === "enable"
      ? await gateway.enableAgent(token, agentId)
      : await gateway.disableAgent(token, agentId, reason || undefined);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=${action}d`);
}

async function grantAclAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const principalId = String(formData.get("principalId") ?? "").trim();
  const accessLevel = String(formData.get("accessLevel") ?? "user");
  if (!agentId || !principalId) redirect(`/admin/agents/${agentId}?error=invalid`);

  const token = await requireSession();
  const res = await gateway.grantAgentAcl(token, agentId, { principalId, accessLevel });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=granted`);
}

async function revokeAclAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const principalId = String(formData.get("principalId") ?? "");
  if (!agentId || !principalId) redirect("/admin/agents");

  const token = await requireSession();
  const res = await gateway.revokeAgentAcl(token, agentId, principalId);
  if (!res.ok) {
    redirect(`/admin/agents/${agentId}?error=revoke-failed`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=revoked`);
}

function formatDateTime(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleString("vi-VN");
  } catch {
    return s;
  }
}

export default async function AgentDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { id } = await params;
  const { ok, error } = await searchParams;

  const token = await requireSession();
  const [detailRes, lifecycleRes] = await Promise.all([
    gateway.getAgent(token, id),
    gateway.listAgentLifecycle(token, id),
  ]);

  if (detailRes.status === 403) redirect("/chat");
  if (detailRes.status === 404) {
    return <p className="text-sm text-red-700">Agent không tồn tại.</p>;
  }

  const detail = (await detailRes.json()) as { agent: AgentRecord; acl: AclEntry[] };
  const lifecycle = lifecycleRes.ok
    ? ((await lifecycleRes.json()) as { items: LifecycleEvent[] }).items
    : [];

  const { agent, acl } = detail;
  const allowedNext = ALLOWED_NEXT[agent.lifecycleState] ?? [];
  const isDisabled = !!agent.disabledAt;

  return (
    <div className="space-y-6">
      <Link
        href="/admin/agents"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Tất cả agents
      </Link>

      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{agent.name}</h1>
          <span
            className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs ${
              LIFECYCLE_BADGES[agent.lifecycleState] ?? "border-gray-300"
            }`}
          >
            {agent.lifecycleState === "production" && <ShieldCheck className="size-3" />}
            {agent.lifecycleState}
          </span>
          {isDisabled && (
            <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700">
              <Power className="size-3" /> killed
            </span>
          )}
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{agent.code}</p>
        {agent.description && <p className="text-sm">{agent.description}</p>}
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

      <div className="grid gap-4 lg:grid-cols-3">
        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">Identity</h2>
          <dl className="space-y-1.5 text-sm">
            <div>
              <dt className="text-[var(--color-muted-fg)]">Type</dt>
              <dd className="font-medium">{agent.type}</dd>
            </div>
            <div>
              <dt className="text-[var(--color-muted-fg)]">Owner</dt>
              <dd className="font-mono text-xs">{agent.ownerUserId ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-[var(--color-muted-fg)]">Sponsor team</dt>
              <dd>{agent.sponsorTeam ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-[var(--color-muted-fg)]">Action tier</dt>
              <dd className="font-medium">{agent.actionTier}</dd>
            </div>
          </dl>
        </section>

        <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <h2 className="font-serif text-lg">Lifecycle</h2>
          {allowedNext.length === 0 ? (
            <p className="text-xs text-[var(--color-muted-fg)]">
              Trạng thái cuối — không thể transition.
            </p>
          ) : (
            <form action={transitionAction} className="space-y-2">
              <input type="hidden" name="agentId" value={agent.id} />
              <div className="space-y-1.5">
                <Label htmlFor="ts">Chuyển sang</Label>
                <select
                  id="ts"
                  name="toState"
                  className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                >
                  {allowedNext.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              </div>
              <Input name="reason" placeholder="Lý do (optional)" />
              <Button type="submit" size="sm" className="w-full">
                Transition
              </Button>
            </form>
          )}
        </section>

        <section
          className={`space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 ${
            isDisabled ? "border-red-300" : ""
          }`}
        >
          <h2 className="font-serif text-lg">Kill Switch</h2>
          {isDisabled ? (
            <>
              <p className="rounded border border-red-200 bg-red-50 p-2 text-xs text-red-700">
                <AlertTriangle className="mr-1 inline size-3" />
                Disabled at {formatDateTime(agent.disabledAt)}
                {agent.disabledReason && (
                  <>
                    <br />
                    Reason: {agent.disabledReason}
                  </>
                )}
              </p>
              <form action={killSwitchAction}>
                <input type="hidden" name="agentId" value={agent.id} />
                <input type="hidden" name="action" value="enable" />
                <Button type="submit" size="sm" variant="outline" className="w-full">
                  Bật lại
                </Button>
              </form>
            </>
          ) : (
            <form action={killSwitchAction} className="space-y-2">
              <input type="hidden" name="agentId" value={agent.id} />
              <input type="hidden" name="action" value="disable" />
              <Input name="reason" placeholder="Lý do tắt agent" />
              <Button type="submit" size="sm" variant="outline" className="w-full">
                <Power className="size-3" /> Disable ngay
              </Button>
            </form>
          )}
        </section>
      </div>

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">
          <UserCheck className="mr-2 inline size-4" /> ACL ({acl.length})
        </h2>
        <div className="overflow-hidden rounded border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Principal</th>
                <th className="px-3 py-2 text-left">Level</th>
                <th className="px-3 py-2 text-left">Granted</th>
                <th className="px-3 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {acl.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3 py-4 text-center text-[var(--color-muted-fg)]">
                    Chưa có ACL.
                  </td>
                </tr>
              ) : (
                acl.map((a) => (
                  <tr key={`${a.principalId}-${a.principalType}`} className="border-t">
                    <td className="px-3 py-2 font-mono text-xs">{a.principalId}</td>
                    <td className="px-3 py-2">
                      <span className="rounded-full border px-2 py-0.5 text-[10px]">
                        {a.accessLevel}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-xs text-[var(--color-muted-fg)]">
                      {formatDateTime(a.grantedAt)}
                    </td>
                    <td className="px-3 py-2 text-right">
                      <form action={revokeAclAction}>
                        <input type="hidden" name="agentId" value={agent.id} />
                        <input type="hidden" name="principalId" value={a.principalId} />
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

        <form action={grantAclAction} className="flex flex-wrap items-end gap-2">
          <input type="hidden" name="agentId" value={agent.id} />
          <div className="flex-1 space-y-1.5">
            <Label htmlFor="pid">User ID (UUID)</Label>
            <Input id="pid" name="principalId" required placeholder="00000000-0000-0000-0000-000000000000" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lvl">Level</Label>
            <select
              id="lvl"
              name="accessLevel"
              className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              defaultValue="user"
            >
              <option value="owner">Owner</option>
              <option value="editor">Editor</option>
              <option value="user">User</option>
            </select>
          </div>
          <Button type="submit" size="sm">
            Grant
          </Button>
        </form>
      </section>

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">
          <History className="mr-2 inline size-4" /> Lifecycle history ({lifecycle.length})
        </h2>
        <ul className="space-y-1.5">
          {lifecycle.length === 0 ? (
            <li className="text-sm text-[var(--color-muted-fg)]">Chưa có transition.</li>
          ) : (
            lifecycle.map((e) => (
              <li key={e.id} className="flex flex-wrap items-baseline gap-2 text-xs">
                <span className="font-medium">
                  {e.fromState ?? "—"} → {e.toState}
                </span>
                <span className="text-[var(--color-muted-fg)]">{formatDateTime(e.occurredAt)}</span>
                {e.reason && <span className="text-[var(--color-muted-fg)]">· {e.reason}</span>}
              </li>
            ))
          )}
        </ul>
      </section>

      <section className="space-y-2">
        <details className="rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <summary className="cursor-pointer text-sm font-medium">Config JSON</summary>
          <pre className="mt-3 max-h-96 overflow-auto whitespace-pre-wrap text-xs">
            {JSON.stringify(JSON.parse(agent.configJson || "{}"), null, 2)}
          </pre>
        </details>
      </section>
    </div>
  );
}
