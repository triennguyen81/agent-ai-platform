import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { AlertOctagon, AlertTriangle, ArrowLeft, FileCheck2, History, Power, Shield, ShieldCheck, UserCheck, Vote, Wrench } from "lucide-react";
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

interface ToolGrant {
  agentId: string;
  toolId: string;
  toolCode: string;
  toolName: string;
  riskTier: string;
  minActionTier: string;
  grantedAt: string;
}

interface ToolRecord {
  id: string;
  code: string;
  name: string;
  riskTier: string;
  minActionTier: string;
  isActive: boolean;
}

interface ReviewBoardRecord {
  id: string;
  tenantId: string;
  name: string;
  quorum: number;
}

interface ReviewRequestRecord {
  id: string;
  agentId: string;
  boardId: string;
  boardName: string;
  requestedBy: string;
  requestedByEmail: string | null;
  targetState: string;
  status: string;
  rationale: string | null;
  decidedAt: string | null;
  createdAt: string;
  approveCount: number;
  rejectCount: number;
  quorum: number;
}

interface HitlGateRecord {
  id: string;
  tenantId: string;
  agentId: string;
  name: string;
  description: string | null;
  triggerRuleJson: string;
  approverRole: string;
  isActive: boolean;
  createdAt: string;
}

const RISK_BADGE: Record<string, string> = {
  low: "border-emerald-300 bg-emerald-50 text-emerald-700",
  medium: "border-amber-300 bg-amber-50 text-amber-700",
  high: "border-orange-300 bg-orange-50 text-orange-700",
  critical: "border-red-300 bg-red-50 text-red-700",
};

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

async function createGateAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  const toolCode = String(formData.get("toolCode") ?? "").trim();
  const fieldPath = String(formData.get("fieldPath") ?? "").trim();
  const op = String(formData.get("op") ?? "gte").trim();
  const value = String(formData.get("value") ?? "").trim();
  const approverRole = String(formData.get("approverRole") ?? "owner").trim();

  if (!agentId || !name) redirect(`/admin/agents/${agentId}?error=invalid`);

  const triggerRule: Record<string, unknown> = {};
  if (toolCode) triggerRule.tool = toolCode;
  if (fieldPath) triggerRule.field_path = fieldPath;
  if (op) triggerRule.op = op;
  if (value) {
    const num = Number(value);
    triggerRule.value = Number.isFinite(num) ? num : value;
  }

  const token = await requireSession();
  const res = await gateway.createAgentGate(token, agentId, {
    name,
    description: description || undefined,
    triggerRule,
    approverRole,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=gate-created`);
}

async function toggleGateAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const gateId = String(formData.get("gateId") ?? "");
  const isActive = String(formData.get("isActive") ?? "") === "true";
  if (!gateId) redirect(`/admin/agents/${agentId}`);

  const token = await requireSession();
  await gateway.updateGate(token, gateId, { isActive: !isActive });
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=gate-toggled`);
}

async function deleteGateAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const gateId = String(formData.get("gateId") ?? "");
  if (!gateId) redirect(`/admin/agents/${agentId}`);

  const token = await requireSession();
  await gateway.deleteGate(token, gateId);
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=gate-deleted`);
}

async function simulateTriggerAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const gateId = String(formData.get("gateId") ?? "");
  const toolCode = String(formData.get("toolCode") ?? "demo_tool");
  if (!gateId) redirect(`/admin/agents/${agentId}`);

  const token = await requireSession();
  // Send a demo payload so the queue has something to vote on
  const res = await fetch(`${process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080"}/hitl/actions`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({
      gateId,
      toolCode,
      payload: { simulated: true, amount: 75000000, note: "Demo trigger from agent detail UI" },
    }),
  });
  if (!res.ok) {
    redirect(`/admin/agents/${agentId}?error=simulate-failed`);
  }
  redirect(`/admin/hitl-queue?ok=simulated`);
}

async function submitReviewAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const boardId = String(formData.get("boardId") ?? "");
  const rationale = String(formData.get("rationale") ?? "");
  if (!agentId || !boardId) redirect(`/admin/agents/${agentId}?error=invalid`);

  const token = await requireSession();
  const res = await gateway.submitReviewRequest(token, agentId, {
    boardId,
    rationale: rationale || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=review-submitted`);
}

async function grantToolAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const toolId = String(formData.get("toolId") ?? "");
  if (!agentId || !toolId) redirect(`/admin/agents/${agentId}?error=invalid`);

  const token = await requireSession();
  const res = await gateway.grantAgentTool(token, agentId, toolId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=tool-granted`);
}

async function revokeToolAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const toolId = String(formData.get("toolId") ?? "");
  if (!agentId || !toolId) redirect("/admin/agents");

  const token = await requireSession();
  const res = await gateway.revokeAgentTool(token, agentId, toolId);
  if (!res.ok) {
    redirect(`/admin/agents/${agentId}?error=tool-revoke-failed`);
  }
  revalidatePath(`/admin/agents/${agentId}`);
  redirect(`/admin/agents/${agentId}?ok=tool-revoked`);
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
  const [detailRes, lifecycleRes, grantsRes, allToolsRes, boardsRes, reviewsRes, gatesRes] = await Promise.all([
    gateway.getAgent(token, id),
    gateway.listAgentLifecycle(token, id),
    gateway.listAgentTools(token, id),
    gateway.listTools(token, true),  // active tools only for the grant dropdown
    gateway.listReviewBoards(token),
    gateway.listAgentReviewRequests(token, id),
    gateway.listAgentGates(token, id),
  ]);

  if (detailRes.status === 403) redirect("/chat");
  if (detailRes.status === 404) {
    return <p className="text-sm text-red-700">Agent không tồn tại.</p>;
  }

  const detail = (await detailRes.json()) as { agent: AgentRecord; acl: AclEntry[] };
  const lifecycle = lifecycleRes.ok
    ? ((await lifecycleRes.json()) as { items: LifecycleEvent[] }).items
    : [];
  const grants: ToolGrant[] = grantsRes.ok
    ? ((await grantsRes.json()) as { items: ToolGrant[] }).items
    : [];
  const allTools: ToolRecord[] = allToolsRes.ok
    ? ((await allToolsRes.json()) as { items: ToolRecord[] }).items
    : [];
  // Tools available to grant: not yet granted AND tier-compatible with this agent
  const grantedIds = new Set(grants.map((g) => g.toolId));
  const TIER_ORDER = ["read_only", "suggest", "execute_with_approval", "autonomous"];
  const agentTierLevel = TIER_ORDER.indexOf(detail.agent.actionTier);
  const grantable = allTools.filter(
    (t) =>
      !grantedIds.has(t.id) &&
      TIER_ORDER.indexOf(t.minActionTier) <= agentTierLevel,
  );

  const boards: ReviewBoardRecord[] = boardsRes.ok
    ? ((await boardsRes.json()) as { items: ReviewBoardRecord[] }).items
    : [];
  const reviews: ReviewRequestRecord[] = reviewsRes.ok
    ? ((await reviewsRes.json()) as { items: ReviewRequestRecord[] }).items
    : [];
  const hasPendingReview = reviews.some((r) => r.status === "pending");
  const canSubmitReview =
    detail.agent.lifecycleState === "pending_review" && !hasPendingReview && boards.length > 0;

  const gates: HitlGateRecord[] = gatesRes.ok
    ? ((await gatesRes.json()) as { items: HitlGateRecord[] }).items
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
          <Wrench className="mr-2 inline size-4" /> Tool Grants ({grants.length})
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Agent chỉ gọi được tools đã grant ở đây. Tools có <code>min_action_tier</code> cao
          hơn <code>{detail.agent.actionTier}</code> sẽ bị ẩn khỏi dropdown grant.
        </p>
        <div className="overflow-hidden rounded border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Tool</th>
                <th className="px-3 py-2 text-left">Risk</th>
                <th className="px-3 py-2 text-left">Min Tier</th>
                <th className="px-3 py-2 text-right">—</th>
              </tr>
            </thead>
            <tbody>
              {grants.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3 py-4 text-center text-[var(--color-muted-fg)]">
                    Chưa grant tool nào — agent này không gọi được tool nào.
                  </td>
                </tr>
              ) : (
                grants.map((g) => (
                  <tr key={g.toolId} className="border-t">
                    <td className="px-3 py-2">
                      <div className="font-medium">{g.toolName}</div>
                      <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">
                        {g.toolCode}
                      </div>
                    </td>
                    <td className="px-3 py-2">
                      <span
                        className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
                          RISK_BADGE[g.riskTier] ?? "border-gray-300"
                        }`}
                      >
                        <Shield className="size-3" />
                        {g.riskTier}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-xs">{g.minActionTier}</td>
                    <td className="px-3 py-2 text-right">
                      <form action={revokeToolAction}>
                        <input type="hidden" name="agentId" value={agent.id} />
                        <input type="hidden" name="toolId" value={g.toolId} />
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

        {grantable.length > 0 && (
          <form action={grantToolAction} className="flex flex-wrap items-end gap-2">
            <input type="hidden" name="agentId" value={agent.id} />
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="tool-sel">Grant tool</Label>
              <select
                id="tool-sel"
                name="toolId"
                required
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              >
                <option value="">— Chọn tool —</option>
                {grantable.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name} [{t.riskTier}] · min: {t.minActionTier}
                  </option>
                ))}
              </select>
            </div>
            <Button type="submit" size="sm">
              Grant
            </Button>
          </form>
        )}
      </section>

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">
          <AlertOctagon className="mr-2 inline size-4" /> HITL Gates ({gates.length})
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Tool calls khớp với <em>trigger rule</em> sẽ bị tạm dừng và đẩy vào{" "}
          <Link href="/admin/hitl-queue" className="underline">
            HITL Queue
          </Link>{" "}
          để con người duyệt trước khi chạy thực.
        </p>

        {gates.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">Chưa có gate nào.</p>
        ) : (
          <div className="overflow-hidden rounded border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-3 py-2 text-left">Name</th>
                  <th className="px-3 py-2 text-left">Trigger Rule</th>
                  <th className="px-3 py-2 text-left">Approver</th>
                  <th className="px-3 py-2 text-left">Status</th>
                  <th className="px-3 py-2 text-right">—</th>
                </tr>
              </thead>
              <tbody>
                {gates.map((g) => (
                  <tr key={g.id} className="border-t align-top">
                    <td className="px-3 py-2">
                      <div className="font-medium">{g.name}</div>
                      {g.description && (
                        <div className="text-[10px] text-[var(--color-muted-fg)]">
                          {g.description}
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <code className="text-[10px]">{g.triggerRuleJson}</code>
                    </td>
                    <td className="px-3 py-2 text-xs">{g.approverRole}</td>
                    <td className="px-3 py-2">
                      {g.isActive ? (
                        <span className="text-xs text-emerald-700">active</span>
                      ) : (
                        <span className="text-xs text-[var(--color-muted-fg)]">disabled</span>
                      )}
                    </td>
                    <td className="px-3 py-2 text-right">
                      <div className="flex justify-end gap-1">
                        <form action={simulateTriggerAction}>
                          <input type="hidden" name="agentId" value={agent.id} />
                          <input type="hidden" name="gateId" value={g.id} />
                          <Button type="submit" size="sm" variant="ghost" title="Tạo pending action demo">
                            Simulate
                          </Button>
                        </form>
                        <form action={toggleGateAction}>
                          <input type="hidden" name="agentId" value={agent.id} />
                          <input type="hidden" name="gateId" value={g.id} />
                          <input type="hidden" name="isActive" value={String(g.isActive)} />
                          <Button type="submit" size="sm" variant="ghost">
                            {g.isActive ? "Tắt" : "Bật"}
                          </Button>
                        </form>
                        <form action={deleteGateAction}>
                          <input type="hidden" name="agentId" value={agent.id} />
                          <input type="hidden" name="gateId" value={g.id} />
                          <Button type="submit" size="sm" variant="ghost">
                            Xoá
                          </Button>
                        </form>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <form action={createGateAction} className="space-y-2 border-t pt-3">
          <input type="hidden" name="agentId" value={agent.id} />
          <div className="grid gap-2 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="g-name">Tên gate</Label>
              <Input id="g-name" name="name" required placeholder="Payment > 50M" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="g-approver">Approver</Label>
              <select
                id="g-approver"
                name="approverRole"
                className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                defaultValue="owner"
              >
                <option value="owner">owner</option>
                <option value="tenant_admin">tenant_admin</option>
              </select>
            </div>
          </div>
          <Input name="description" placeholder="Mô tả (optional)" />
          <div className="grid gap-2 sm:grid-cols-4">
            <Input name="toolCode" placeholder="tool: send_payment" />
            <Input name="fieldPath" placeholder="field: amount" />
            <select
              name="op"
              className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
              defaultValue="gte"
            >
              <option value="eq">eq</option>
              <option value="gte">gte</option>
              <option value="gt">gt</option>
              <option value="lte">lte</option>
              <option value="lt">lt</option>
            </select>
            <Input name="value" placeholder="value: 50000000" />
          </div>
          <Button type="submit" size="sm" className="w-full">
            <AlertOctagon className="size-4" /> Tạo HITL gate
          </Button>
        </form>
      </section>

      <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <h2 className="font-serif text-lg">
          <Vote className="mr-2 inline size-4" /> Review Requests ({reviews.length})
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Promotion to production cần qua review board. Owner không thể tự promote.
        </p>

        {reviews.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">Chưa có request nào.</p>
        ) : (
          <ul className="space-y-2">
            {reviews.map((r) => {
              const badge =
                r.status === "approved"
                  ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                  : r.status === "rejected"
                  ? "border-red-300 bg-red-50 text-red-700"
                  : r.status === "withdrawn"
                  ? "border-gray-300 bg-gray-50 text-gray-700"
                  : "border-amber-300 bg-amber-50 text-amber-700";
              return (
                <li
                  key={r.id}
                  className="flex flex-wrap items-start justify-between gap-2 rounded border p-3 text-sm"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className={`rounded-full border px-2 py-0.5 text-[10px] ${badge}`}>
                        {r.status}
                      </span>
                      <span className="text-xs text-[var(--color-muted-fg)]">
                        target: {r.targetState}
                      </span>
                      <span className="text-xs text-[var(--color-muted-fg)]">
                        · board: {r.boardName}
                      </span>
                    </div>
                    {r.rationale && <p className="mt-1 text-xs">{r.rationale}</p>}
                    <p className="mt-1 text-[10px] text-[var(--color-muted-fg)]">
                      By {r.requestedByEmail ?? r.requestedBy} · {formatDateTime(r.createdAt)}
                    </p>
                  </div>
                  <div className="shrink-0 text-right text-xs">
                    <div>
                      ✓ {r.approveCount} / quorum {r.quorum}
                    </div>
                    {r.rejectCount > 0 && (
                      <div className="text-red-700">✗ {r.rejectCount}</div>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}

        {canSubmitReview && (
          <form action={submitReviewAction} className="space-y-2 border-t pt-3">
            <input type="hidden" name="agentId" value={agent.id} />
            <div className="flex flex-wrap gap-2">
              <div className="flex-1 space-y-1.5">
                <Label htmlFor="board-sel">Review Board</Label>
                <select
                  id="board-sel"
                  name="boardId"
                  required
                  className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
                >
                  {boards.map((b) => (
                    <option key={b.id} value={b.id}>
                      {b.name} (quorum {b.quorum})
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <Input name="rationale" placeholder="Lý do submit (optional)..." />
            <Button type="submit" size="sm" className="w-full">
              <FileCheck2 className="size-4" /> Submit for production review
            </Button>
          </form>
        )}

        {detail.agent.lifecycleState !== "pending_review" && (
          <p className="text-xs text-[var(--color-muted-fg)]">
            Agent phải ở lifecycle <code>pending_review</code> để submit. Hiện đang:{" "}
            <code>{detail.agent.lifecycleState}</code>.
          </p>
        )}

        {hasPendingReview && (
          <p className="text-xs text-amber-700">
            ⚠ Đang có request pending — đợi vote xong trước khi submit mới.
          </p>
        )}
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
