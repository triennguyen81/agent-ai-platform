import Link from "next/link";
import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import {
  ArrowLeft,
  Building2,
  CheckCircle2,
  FlaskConical,
  History,
  Power,
  Rocket,
  ShieldCheck,
  Wrench,
} from "lucide-react";
import { agent, gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { AgentTestPanel } from "@/components/agent-test-panel";
import { CapabilityPicker, type CapabilityOption } from "@/components/capability-picker";

interface SystemAgentRecord {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  type: string;
  actionTier: string;
  lifecycleState: string;
  enabled: boolean;
  currentDraftVersionId: string | null;
  publishedVersionId: string | null;
  createdAt: string;
  updatedAt: string;
}

interface AgentVersionRecord {
  id: string;
  agentId: string;
  version: number;
  status: string;
  provider: string;
  model: string;
  systemPrompt: string;
  temperature: number;
  maxTurns: number;
  timeoutSeconds: number;
  maxTokens: number;
  memoryConfigJson: string;
  createdAt: string;
  publishedAt: string | null;
  toolIds: string[];
  skills: string[];
  workflowId: string | null;
  delegateAgentIds: string[];
  capabilityIds: string[];
}
interface WorkflowRecord { id: string; name: string; isActive: boolean }

interface ToolRecord {
  id: string;
  code: string;
  name: string;
  riskTier: string;
  minActionTier: string;
  isActive: boolean;
}

interface TenantRecord {
  id: string;
  code: string;
  name: string;
  status: string;
}

interface AgentTenantAssignmentRecord {
  tenantId: string;
  tenantCode: string;
  tenantName: string;
  assignedAt: string;
}

interface SkillSummary {
  name: string;
  description: string;
  keywords: string[];
  always_on: boolean;
  requires_knowledge_context: boolean;
}

function formatDateTime(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleString("vi-VN");
  } catch {
    return s;
  }
}

function parseChecked(formData: FormData, name: string): string[] {
  return formData.getAll(name).map(String).filter(Boolean);
}

function draftPayloadFromForm(formData: FormData): Record<string, unknown> {
  return {
    provider: String(formData.get("provider") ?? "ollama"),
    model: String(formData.get("model") ?? "").trim(),
    systemPrompt: String(formData.get("systemPrompt") ?? "").trim(),
    temperature: Number(formData.get("temperature") ?? 0.2),
    maxTurns: Number(formData.get("maxTurns") ?? 4),
    timeoutSeconds: Number(formData.get("timeoutSeconds") ?? 120),
    maxTokens: Number(formData.get("maxTokens") ?? 2048),
    memoryConfig: { mode: String(formData.get("memoryMode") ?? "none") },
    toolIds: parseChecked(formData, "toolIds"),
    skills: parseChecked(formData, "skills"),
    workflowId: String(formData.get("workflowId") ?? "") || null,
    clearWorkflow: !String(formData.get("workflowId") ?? ""),
    delegateAgentIds: parseChecked(formData, "delegateAgentIds"),
    capabilityIds: parseChecked(formData, "capabilityIds"),
  };
}

async function updateIdentityAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  if (!agentId) redirect("/admin/agent-builder");
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  const actionTier = String(formData.get("actionTier") ?? "").trim();

  const token = await requireSession();
  const res = await gateway.updateSystemAgent(token, agentId, {
    name: name || undefined,
    description: description || undefined,
    actionTier: actionTier || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=identity`);
  }
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=identity-updated&tab=identity`);
}

async function toggleEnabledAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!agentId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res =
    action === "enable"
      ? await gateway.enableSystemAgent(token, agentId)
      : await gateway.disableSystemAgent(token, agentId);
  if (!res.ok) redirect(`/admin/agent-builder/${agentId}?error=toggle-failed&tab=identity`);
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=${action}d&tab=identity`);
}

async function createDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  if (!agentId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.createSystemAgentVersion(token, agentId, draftPayloadFromForm(formData));
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=runtime`);
  }
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=draft-saved&tab=runtime`);
}

async function updateDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.updateSystemAgentVersion(token, agentId, versionId, draftPayloadFromForm(formData));
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=runtime`);
  }
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=draft-saved&tab=runtime`);
}

async function validateDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.validateSystemAgentVersion(token, agentId, versionId);
  if (!res.ok) redirect(`/admin/agent-builder/${agentId}?error=validate-failed&tab=runtime`);
  const result = (await res.json()) as { valid: boolean; errors: string[] };
  if (!result.valid) {
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(result.errors.join(", "))}&tab=runtime`);
  }
  redirect(`/admin/agent-builder/${agentId}?ok=valid&tab=runtime`);
}

async function publishDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.publishSystemAgentVersion(token, agentId, versionId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(body.slice(0, 300))}&tab=runtime`);
  }
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=published&tab=versions`);
}

async function assignTenantAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const tenantId = String(formData.get("tenantId") ?? "");
  if (!agentId || !tenantId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.assignSystemAgentTenant(token, agentId, tenantId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/admin/agent-builder/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=tenants`);
  }
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=tenant-assigned&tab=tenants`);
}

async function revokeTenantAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const tenantId = String(formData.get("tenantId") ?? "");
  if (!agentId || !tenantId) redirect("/admin/agent-builder");

  const token = await requireSession();
  const res = await gateway.revokeSystemAgentTenant(token, agentId, tenantId);
  if (!res.ok) redirect(`/admin/agent-builder/${agentId}?error=tenant-revoke-failed&tab=tenants`);
  revalidatePath(`/admin/agent-builder/${agentId}`);
  redirect(`/admin/agent-builder/${agentId}?ok=tenant-revoked&tab=tenants`);
}

const selectClass =
  "w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm";
const textareaClass = selectClass;

function DraftForm({
  agentId,
  version,
  tools,
  skills,
  capabilities,
  workflows,
  delegates,
  formAction,
}: {
  agentId: string;
  version: AgentVersionRecord | null;
  tools: ToolRecord[];
  skills: SkillSummary[];
  capabilities: CapabilityOption[];
  workflows: WorkflowRecord[];
  delegates: SystemAgentRecord[];
  formAction: (formData: FormData) => Promise<void>;
}) {
  const memoryMode = (() => {
    try {
      return (JSON.parse(version?.memoryConfigJson || "{}").mode as string) ?? "none";
    } catch {
      return "none";
    }
  })();

  return (
    <form action={formAction} className="space-y-4">
      <input type="hidden" name="agentId" value={agentId} />
      {version && <input type="hidden" name="versionId" value={version.id} />}

      {/* forceMount keeps every field in the DOM (and thus in FormData) no
          matter which of these three tabs is currently visible — they all
          belong to the same single save action below. */}
      <TabsContent value="runtime" forceMount className="mt-0 space-y-4 data-[state=inactive]:hidden">
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="d-provider">Provider</Label>
            <select id="d-provider" name="provider" defaultValue={version?.provider ?? "ollama"} className={selectClass}>
              <option value="ollama">Ollama</option>
              <option value="claude_sdk">Claude SDK (subscription)</option>
            </select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="d-model">Model</Label>
            <Input
              id="d-model"
              name="model"
              required
              defaultValue={version?.model ?? ""}
              placeholder="llama3.1:8b hoặc claude-haiku-4-5-20251001"
            />
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="d-prompt">System prompt</Label>
          <textarea
            id="d-prompt"
            name="systemPrompt"
            required
            rows={6}
            defaultValue={version?.systemPrompt ?? ""}
            className={textareaClass}
            placeholder="Bạn là trợ lý..."
          />
        </div>

        <div className="grid gap-3 sm:grid-cols-4">
          <div className="space-y-1.5">
            <Label htmlFor="d-temp">Temperature</Label>
            <Input id="d-temp" name="temperature" type="number" step="0.1" min={0} max={2} defaultValue={version?.temperature ?? 0.2} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="d-turns">Max turns</Label>
            <Input id="d-turns" name="maxTurns" type="number" min={1} max={20} defaultValue={version?.maxTurns ?? 4} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="d-timeout">Timeout (s)</Label>
            <Input id="d-timeout" name="timeoutSeconds" type="number" min={1} max={3600} defaultValue={version?.timeoutSeconds ?? 120} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="d-tokens">Max tokens</Label>
            <Input id="d-tokens" name="maxTokens" type="number" min={1} max={1000000} defaultValue={version?.maxTokens ?? 2048} />
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="d-memory">Memory</Label>
          <select id="d-memory" name="memoryMode" defaultValue={memoryMode} className={`${selectClass} sm:w-64`}>
            <option value="none">Không nhớ (mỗi lượt độc lập)</option>
            <option value="session">Nhớ trong phiên hội thoại</option>
          </select>
        </div>
      </TabsContent>

      <TabsContent value="capabilities" forceMount className="mt-0 space-y-4 data-[state=inactive]:hidden">
        <div className="space-y-2">
          <Label>
            <Building2 className="mr-1 inline size-3.5" /> Nghiệp vụ hỗ trợ
          </Label>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Publish sẽ đóng băng danh sách nghiệp vụ này — draft mới tạo tiếp theo sẽ giữ nguyên lựa chọn hiện tại làm mặc định.
          </p>
          <CapabilityPicker capabilities={capabilities} selected={version?.capabilityIds ?? []} />
        </div>

        <div className="space-y-2">
          <Label>
            <Wrench className="mr-1 inline size-3.5" /> Tools được cấp
          </Label>
          {tools.length === 0 ? (
            <p className="text-xs text-[var(--color-muted-fg)]">Chưa có tool nào trong hệ thống.</p>
          ) : (
            <div className="grid gap-1.5 rounded border p-3 sm:grid-cols-2">
              {tools.map((t) => (
                <label key={t.id} className="flex items-center gap-2 text-sm">
                  <input type="checkbox" name="toolIds" value={t.id} defaultChecked={version?.toolIds.includes(t.id) ?? false} />
                  <span>{t.name}</span>
                  <span className="text-[10px] text-[var(--color-muted-fg)]">[{t.riskTier}]</span>
                </label>
              ))}
            </div>
          )}
        </div>

        <div className="space-y-2">
          <Label>Skills được gán</Label>
          {skills.length === 0 ? (
            <p className="text-xs text-[var(--color-muted-fg)]">Chưa có skill nào cài đặt trên runtime.</p>
          ) : (
            <div className="grid gap-1.5 rounded border p-3 sm:grid-cols-2">
              {skills.map((s) => (
                <label key={s.name} className="flex items-center gap-2 text-sm">
                  <input type="checkbox" name="skills" value={s.name} defaultChecked={version?.skills.includes(s.name) ?? false} />
                  <span>{s.name}</span>
                </label>
              ))}
            </div>
          )}
        </div>
      </TabsContent>

      <TabsContent value="workflow" forceMount className="mt-0 space-y-4 data-[state=inactive]:hidden">
        <div className="space-y-2">
          <Label htmlFor="d-workflow">Workflow</Label>
          <select id="d-workflow" name="workflowId" defaultValue={version?.workflowId ?? ""} className={selectClass}>
            <option value="">Không dùng workflow</option>
            {workflows.filter((workflow) => workflow.isActive).map((workflow) => (
              <option key={workflow.id} value={workflow.id}>{workflow.name}</option>
            ))}
          </select>
        </div>
        <div className="space-y-2">
          <Label>Delegate specialists</Label>
          <div className="max-h-48 space-y-1 overflow-auto rounded border p-3">
            {delegates.map((delegate) => (
              <label key={delegate.id} className="flex items-center gap-2 text-sm">
                <input type="checkbox" name="delegateAgentIds" value={delegate.id} defaultChecked={version?.delegateAgentIds?.includes(delegate.id) ?? false} />
                <span>{delegate.name}</span>
                <span className="text-[10px] text-[var(--color-muted-fg)]">{delegate.lifecycleState}</span>
              </label>
            ))}
            {delegates.length === 0 && <p className="text-xs text-[var(--color-muted-fg)]">Chưa có agent đã publish nào khác để làm delegate.</p>}
          </div>
        </div>
      </TabsContent>

      <div className="flex flex-wrap items-center gap-2 border-t border-[var(--color-border)] pt-4">
        <Button type="submit" size="sm">
          {version ? "Lưu draft" : "Tạo draft"}
        </Button>
        {version && (
          <>
            <Button type="submit" form="validate-draft-form" size="sm" variant="outline">
              <ShieldCheck className="size-3.5" /> Validate
            </Button>
            <Button type="submit" form="publish-draft-form" size="sm" variant="outline">
              <Rocket className="size-3.5" /> Publish v{version.version}
            </Button>
          </>
        )}
      </div>
    </form>
  );
}

export default async function AgentBuilderDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string; tab?: string }>;
}) {
  const { id } = await params;
  const { ok, error, tab } = await searchParams;
  const token = await requireSession();

  const [detailRes, versionsRes, toolsRes, skillsRes, allTenantsRes, agentTenantsRes, workflowsRes, agentsRes, capabilitiesRes] = await Promise.all([
    gateway.getSystemAgent(token, id),
    gateway.listSystemAgentVersions(token, id),
    gateway.listTools(token, true),
    agent.listSkills(),
    gateway.listTenants(token),
    gateway.listSystemAgentTenants(token, id),
    gateway.listWorkflows(token),
    gateway.listSystemAgents(token),
    gateway.listCapabilityCatalog(token),
  ]);

  if (detailRes.status === 403) redirect("/chat");
  if (detailRes.status === 404) {
    return <p className="text-sm text-red-700">Agent không tồn tại.</p>;
  }

  const sysAgent = (await detailRes.json()) as SystemAgentRecord;
  const versions: AgentVersionRecord[] = versionsRes.ok
    ? ((await versionsRes.json()) as { items: AgentVersionRecord[] }).items
    : [];
  const tools: ToolRecord[] = toolsRes.ok ? ((await toolsRes.json()) as { items: ToolRecord[] }).items : [];
  const skills: SkillSummary[] = skillsRes.ok ? ((await skillsRes.json()) as SkillSummary[]) : [];
  const allTenants: TenantRecord[] = allTenantsRes.ok
    ? ((await allTenantsRes.json()) as { items: TenantRecord[] }).items
    : [];
  const assignedTenants: AgentTenantAssignmentRecord[] = agentTenantsRes.ok
    ? ((await agentTenantsRes.json()) as { items: AgentTenantAssignmentRecord[] }).items
    : [];
  const assignedTenantIds = new Set(assignedTenants.map((t) => t.tenantId));
  const workflows: WorkflowRecord[] = workflowsRes.ok ? ((await workflowsRes.json()) as { items: WorkflowRecord[] }).items : [];
  const delegates: SystemAgentRecord[] = agentsRes.ok ? ((await agentsRes.json()) as { items: SystemAgentRecord[] }).items.filter((candidate) => candidate.id !== id && candidate.enabled && candidate.lifecycleState === "production" && Boolean(candidate.publishedVersionId)) : [];
  const capabilities: CapabilityOption[] = capabilitiesRes.ok
    ? ((await capabilitiesRes.json()) as { items: CapabilityOption[] }).items
    : [];

  const draftVersion = sysAgent.currentDraftVersionId
    ? versions.find((v) => v.id === sysAgent.currentDraftVersionId) ?? null
    : null;
  const publishedVersion = sysAgent.publishedVersionId
    ? versions.find((v) => v.id === sysAgent.publishedVersionId) ?? null
    : null;
  const testableLabel = draftVersion
    ? `Đang test bản draft v${draftVersion.version} (chưa publish)`
    : publishedVersion
    ? `Đang test bản đã publish v${publishedVersion.version}`
    : null;
  const activeVersion = draftVersion ?? publishedVersion;
  const draftAction = draftVersion ? updateDraftAction : createDraftAction;

  const TABS = [
    { value: "identity", label: "Identity" },
    { value: "runtime", label: "Runtime" },
    { value: "capabilities", label: "Nghiệp vụ" },
    { value: "workflow", label: "Workflow / Delegates" },
    { value: "tenants", label: `Tenant Assignment (${assignedTenants.length})` },
    { value: "versions", label: `Version History (${versions.length})` },
    { value: "test", label: "Test" },
  ] as const;
  const defaultTab = TABS.some((t) => t.value === tab) ? (tab as string) : "identity";

  return (
    <div className="space-y-6">
      <Link
        href="/admin/agent-builder"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Tất cả agents
      </Link>

      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{sysAgent.name}</h1>
          {sysAgent.lifecycleState === "production" && (
            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">
              <ShieldCheck className="size-3" /> production
            </span>
          )}
          {!sysAgent.enabled && (
            <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700">
              <Power className="size-3" /> disabled
            </span>
          )}
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{sysAgent.slug}</p>
        {sysAgent.description && <p className="text-sm">{sysAgent.description}</p>}
      </header>

      {ok && (
        <p className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
          <CheckCircle2 className="mr-1 inline size-4" />
          {ok === "valid" ? "Draft hợp lệ." : "Thành công."}
        </p>
      )}
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {decodeURIComponent(error)}
        </p>
      )}

      <Tabs defaultValue={defaultTab}>
        <TabsList>
          {TABS.map((t) => (
            <TabsTrigger key={t.value} value={t.value}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>

        <TabsContent value="identity" className="grid gap-4 lg:grid-cols-3">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">Identity</h2>
            <form action={updateIdentityAction} className="space-y-2">
              <input type="hidden" name="agentId" value={sysAgent.id} />
              <div className="space-y-1.5">
                <Label htmlFor="i-name">Tên</Label>
                <Input id="i-name" name="name" defaultValue={sysAgent.name} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="i-desc">Mô tả</Label>
                <textarea id="i-desc" name="description" rows={2} defaultValue={sysAgent.description ?? ""} className={textareaClass} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="i-tier">Action Tier</Label>
                <select id="i-tier" name="actionTier" defaultValue={sysAgent.actionTier} className={selectClass}>
                  <option value="read_only">Read-only</option>
                  <option value="suggest">Suggest</option>
                  <option value="execute_with_approval">Execute with approval</option>
                  <option value="autonomous">Autonomous</option>
                </select>
              </div>
              <Button type="submit" size="sm" className="w-full">
                Lưu identity
              </Button>
            </form>
          </section>

          <section className={`space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 ${!sysAgent.enabled ? "border-red-300" : ""}`}>
            <h2 className="font-serif text-lg">Kill Switch</h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Disable dừng agent ngay với mọi tenant, không cần rollback version.
            </p>
            <form action={toggleEnabledAction}>
              <input type="hidden" name="agentId" value={sysAgent.id} />
              <input type="hidden" name="action" value={sysAgent.enabled ? "disable" : "enable"} />
              <Button type="submit" size="sm" variant="outline" className="w-full">
                <Power className="size-3.5" /> {sysAgent.enabled ? "Disable" : "Enable lại"}
              </Button>
            </form>
          </section>

          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">Trạng thái</h2>
            <dl className="space-y-1.5 text-sm">
              <div>
                <dt className="text-[var(--color-muted-fg)]">Draft hiện tại</dt>
                <dd className="font-medium">{draftVersion ? `v${draftVersion.version} (${draftVersion.status})` : "—"}</dd>
              </div>
              <div>
                <dt className="text-[var(--color-muted-fg)]">Đang publish</dt>
                <dd className="font-medium">{publishedVersion ? `v${publishedVersion.version}` : "chưa publish"}</dd>
              </div>
            </dl>
          </section>
        </TabsContent>

        {/* Runtime / Capabilities / Workflow share DraftForm's single <form>;
            its TabsContent panels use forceMount so switching tabs never
            drops unsaved field values. */}
        <div>
          {!draftVersion && (
            <p className="mb-3 text-xs text-[var(--color-muted-fg)]">
              {publishedVersion
                ? `Không có draft đang mở. Lưu ở đây sẽ tạo v${publishedVersion.version + 1} — điền sẵn từ bản đã publish, sửa rồi lưu.`
                : "Chưa có version nào. Điền cấu hình rồi lưu để tạo draft đầu tiên."}
            </p>
          )}
          {draftVersion && (
            <p className="mb-3 text-xs text-[var(--color-muted-fg)]">
              Đang sửa draft v{draftVersion.version}. Version đã publish là bất biến — chỉ draft mới sửa được.
            </p>
          )}
          <DraftForm agentId={sysAgent.id} version={activeVersion} tools={tools} skills={skills} capabilities={capabilities} workflows={workflows} delegates={delegates} formAction={draftAction} />
        </div>

        {draftVersion && (
          <>
            <form id="validate-draft-form" action={validateDraftAction} className="hidden">
              <input type="hidden" name="agentId" value={sysAgent.id} />
              <input type="hidden" name="versionId" value={draftVersion.id} />
            </form>
            <form id="publish-draft-form" action={publishDraftAction} className="hidden">
              <input type="hidden" name="agentId" value={sysAgent.id} />
              <input type="hidden" name="versionId" value={draftVersion.id} />
            </form>
          </>
        )}

        <TabsContent value="tenants">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">
              <Building2 className="mr-2 inline size-4" /> Tenant Assignment
            </h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Agent chỉ chạy được cho tenant nào đã assign ở đây. Một agent có thể assign cho nhiều
              tenant. Sau khi assign, tenant ADMIN sẽ tự gán agent cho user trong tenant của họ.
              {!publishedVersion && " Agent chưa publish version nào — user sẽ không dùng được cho tới khi publish."}
            </p>
            {allTenants.length === 0 ? (
              <p className="text-sm text-[var(--color-muted-fg)]">Chưa có tenant nào trong hệ thống.</p>
            ) : (
              <div className="overflow-hidden rounded border">
                <table className="w-full text-sm">
                  <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                    <tr>
                      <th className="px-3 py-2 text-left">Tenant</th>
                      <th className="px-3 py-2 text-left">Trạng thái</th>
                      <th className="px-3 py-2 text-right">—</th>
                    </tr>
                  </thead>
                  <tbody>
                    {allTenants.map((t) => {
                      const assigned = assignedTenantIds.has(t.id);
                      return (
                        <tr key={t.id} className="border-t align-top">
                          <td className="px-3 py-2">
                            <div className="font-medium">{t.name}</div>
                            <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{t.code}</div>
                          </td>
                          <td className="px-3 py-2">
                            {assigned ? (
                              <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-[10px] text-emerald-700">
                                <CheckCircle2 className="size-3" /> assigned
                              </span>
                            ) : (
                              <span className="rounded-full border border-gray-300 bg-gray-50 px-2 py-0.5 text-[10px] text-gray-600">
                                unassigned
                              </span>
                            )}
                          </td>
                          <td className="px-3 py-2 text-right">
                            {assigned ? (
                              <form action={revokeTenantAction}>
                                <input type="hidden" name="agentId" value={sysAgent.id} />
                                <input type="hidden" name="tenantId" value={t.id} />
                                <Button type="submit" size="sm" variant="ghost">
                                  Revoke
                                </Button>
                              </form>
                            ) : (
                              <form action={assignTenantAction}>
                                <input type="hidden" name="agentId" value={sysAgent.id} />
                                <input type="hidden" name="tenantId" value={t.id} />
                                <Button type="submit" size="sm">
                                  Assign
                                </Button>
                              </form>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </TabsContent>

        <TabsContent value="versions">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">
              <History className="mr-2 inline size-4" /> Version History
            </h2>
            {versions.length === 0 ? (
              <p className="text-sm text-[var(--color-muted-fg)]">Chưa có version nào.</p>
            ) : (
              <div className="overflow-hidden rounded border">
                <table className="w-full text-sm">
                  <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                    <tr>
                      <th className="px-3 py-2 text-left">Version</th>
                      <th className="px-3 py-2 text-left">Status</th>
                      <th className="px-3 py-2 text-left">Provider / Model</th>
                      <th className="px-3 py-2 text-left">Published</th>
                    </tr>
                  </thead>
                  <tbody>
                    {versions.map((v) => (
                      <tr key={v.id} className="border-t align-top">
                        <td className="px-3 py-2 font-medium">v{v.version}</td>
                        <td className="px-3 py-2">
                          <span
                            className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] ${
                              v.status === "published"
                                ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                                : "border-gray-300 bg-gray-50 text-gray-700"
                            }`}
                          >
                            {v.status}
                            {v.status === "published" && " · read-only"}
                          </span>
                        </td>
                        <td className="px-3 py-2 text-xs">
                          {v.provider} · <span className="font-mono">{v.model}</span>
                        </td>
                        <td className="px-3 py-2 text-xs text-[var(--color-muted-fg)]">{formatDateTime(v.publishedAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </TabsContent>

        <TabsContent value="test">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">
              <FlaskConical className="mr-2 inline size-4" /> Test
            </h2>
            {testableLabel ? (
              <>
                <p className="text-xs text-[var(--color-muted-fg)]">
                  {testableLabel}. Chạy qua đúng pipeline runtime thật (provider/model/prompt/tools/skills từ database) — không đi qua tenant/user assignment nên chỉ SUPER_ADMIN gọi được.
                </p>
                <AgentTestPanel agentId={sysAgent.id} />
              </>
            ) : (
              <p className="text-sm text-[var(--color-muted-fg)]">Lưu draft trước để có thể test.</p>
            )}
          </section>
        </TabsContent>
      </Tabs>
    </div>
  );
}
