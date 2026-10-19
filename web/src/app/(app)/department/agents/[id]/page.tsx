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
  Users,
  Wrench,
} from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { AgentTestPanel } from "@/components/agent-test-panel";
import { CapabilityPicker, type CapabilityOption } from "@/components/capability-picker";
import {
  DocumentProcessingEditor,
  type CatalogDocumentType,
  type DocumentTypeConfig,
} from "@/components/document-processing-editor";

// ACL V2-C: ADMIN's own Department Agent Builder. Structurally the same
// editing experience as SUPER_ADMIN's /admin/agent-builder/[id] (draft ->
// validate -> test -> publish, immutable published versions), adapted for
// one difference in kind: instead of a Tenant Assignment tab (SUPER_ADMIN
// hands a platform template out to tenants), this page has a Nhân sự tab
// (ADMIN hands its OWN agent to USERs in its OWN department) — ownership
// here is intrinsic to the agent row itself, there is no separate
// entitlement step. Every gateway.*DepartmentAgent* call is scoped
// server-side to the caller's own tenant+department; no id from this page
// ever authorizes a cross-department read/write.

interface DepartmentAgentRecord {
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
interface WorkflowRecord { id: string; name: string; isActive: boolean; scope?: "platform" | "department" }

interface DepartmentSkillRecord { id: string; code: string; name: string; isActive: boolean; scope: "platform" | "department" }

interface AttachedKnowledgeRecord { id: string; title: string; tags: string[]; scope: "platform" | "department" }
interface AttachableKnowledgeRecord { id: string; title: string; tags: string[]; scope: "platform" | "department" }

interface ToolRecord {
  id: string;
  code: string;
  name: string;
  riskTier: string;
  minActionTier: string;
  isActive: boolean;
}

interface DepartmentUserRecord {
  id: string;
  email: string;
  displayName: string | null;
  isActive: boolean;
}

interface AgentUserAssignmentRecord {
  userId: string;
  email: string;
  displayName: string | null;
  assignedAt: string;
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
  if (!agentId) redirect("/department/agents");
  const name = String(formData.get("name") ?? "").trim();
  const description = String(formData.get("description") ?? "").trim();
  const actionTier = String(formData.get("actionTier") ?? "").trim();

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.updateDepartmentAgent(viewer.token, agentId, {
    name: name || undefined,
    description: description || undefined,
    actionTier: actionTier || undefined,
  });
  if (!res.ok) {
    const body = await res.text();
    redirect(`/department/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=identity`);
  }
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=identity-updated&tab=identity`);
}

async function toggleEnabledAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!agentId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res =
    action === "enable"
      ? await gateway.enableDepartmentAgent(viewer.token, agentId)
      : await gateway.disableDepartmentAgent(viewer.token, agentId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=toggle-failed&tab=identity`);
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=${action}d&tab=identity`);
}

async function createDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  if (!agentId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentAgentVersion(viewer.token, agentId, draftPayloadFromForm(formData));
  if (!res.ok) {
    const body = await res.text();
    redirect(`/department/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=runtime`);
  }
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=draft-saved&tab=runtime`);
}

async function updateDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.updateDepartmentAgentVersion(viewer.token, agentId, versionId, draftPayloadFromForm(formData));
  if (!res.ok) {
    const body = await res.text();
    redirect(`/department/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 200))}&tab=runtime`);
  }
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=draft-saved&tab=runtime`);
}

async function validateDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.validateDepartmentAgentVersion(viewer.token, agentId, versionId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=validate-failed&tab=runtime`);
  const result = (await res.json()) as { valid: boolean; errors: string[] };
  if (!result.valid) {
    redirect(`/department/agents/${agentId}?error=${encodeURIComponent(result.errors.join(", "))}&tab=runtime`);
  }
  redirect(`/department/agents/${agentId}?ok=valid&tab=runtime`);
}

async function publishDraftAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!agentId || !versionId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.publishDepartmentAgentVersion(viewer.token, agentId, versionId);
  if (!res.ok) {
    const body = await res.text();
    redirect(`/department/agents/${agentId}?error=${encodeURIComponent(body.slice(0, 300))}&tab=runtime`);
  }
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=published&tab=versions`);
}

async function assignUserAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const userId = String(formData.get("userId") ?? "");
  if (!agentId || !userId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.assignDepartmentAgentUser(viewer.token, agentId, userId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=assign-failed&tab=users`);
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=user-assigned&tab=users`);
}

async function revokeUserAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const userId = String(formData.get("userId") ?? "");
  if (!agentId || !userId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.revokeDepartmentAgentUser(viewer.token, agentId, userId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=revoke-failed&tab=users`);
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=user-revoked&tab=users`);
}

async function attachKnowledgeAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const noteId = String(formData.get("noteId") ?? "");
  if (!agentId || !noteId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.attachDepartmentAgentKnowledge(viewer.token, agentId, noteId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=attach-failed&tab=knowledge`);
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=knowledge-attached&tab=knowledge`);
}

async function detachKnowledgeAction(formData: FormData): Promise<void> {
  "use server";
  const agentId = String(formData.get("agentId") ?? "");
  const noteId = String(formData.get("noteId") ?? "");
  if (!agentId || !noteId) redirect("/department/agents");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.detachDepartmentAgentKnowledge(viewer.token, agentId, noteId);
  if (!res.ok) redirect(`/department/agents/${agentId}?error=detach-failed&tab=knowledge`);
  revalidatePath(`/department/agents/${agentId}`);
  redirect(`/department/agents/${agentId}?ok=knowledge-detached&tab=knowledge`);
}

const selectClass =
  "w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm";
const textareaClass = selectClass;

function DraftForm({
  agentId,
  version,
  tools,
  ownSkills,
  capabilities,
  workflows,
  delegates,
  formAction,
}: {
  agentId: string;
  version: AgentVersionRecord | null;
  tools: ToolRecord[];
  ownSkills: DepartmentSkillRecord[];
  capabilities: CapabilityOption[];
  workflows: WorkflowRecord[];
  delegates: { id: string; name: string; lifecycleState: string }[];
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
            Danh mục nghiệp vụ nền tảng — chỉ xem/chọn được, tạo/sửa nghiệp vụ mới thuộc phần quản trị nền tảng.
            Publish sẽ đóng băng danh sách này — draft mới tạo tiếp theo sẽ giữ nguyên lựa chọn hiện tại làm mặc định.
          </p>
          <CapabilityPicker capabilities={capabilities} selected={version?.capabilityIds ?? []} />
        </div>

        <div className="space-y-2">
          <Label>
            <Wrench className="mr-1 inline size-3.5" /> Tools được cấp
          </Label>
          {tools.length === 0 ? (
            <p className="text-xs text-[var(--color-muted-fg)]">Chưa có tool nào được duyệt.</p>
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
          {ownSkills.length === 0 ? (
            <p className="text-xs text-[var(--color-muted-fg)]">Chưa có skill nào khả dụng.</p>
          ) : (
            <div className="grid gap-1.5 rounded border p-3 sm:grid-cols-2">
              {ownSkills.filter((s) => s.isActive).map((s) => (
                <label key={s.id} className="flex items-center gap-2 text-sm">
                  <input type="checkbox" name="skills" value={s.code} defaultChecked={version?.skills.includes(s.code) ?? false} />
                  <span>{s.name}</span>
                  <span className="rounded-full bg-[var(--color-muted)] px-1.5 py-0.5 text-[10px]">{s.scope === "department" ? "Phòng ban" : "Hệ thống"}</span>
                </label>
              ))}
            </div>
          )}
        </div>
      </TabsContent>

      <TabsContent value="workflow" forceMount className="mt-0 space-y-4 data-[state=inactive]:hidden">
        <div className="space-y-2">
          <Label htmlFor="d-workflow">Workflow</Label>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Chọn workflow nền tảng, hoặc workflow đã công bố (published) của phòng ban bạn.
          </p>
          <select id="d-workflow" name="workflowId" defaultValue={version?.workflowId ?? ""} className={selectClass}>
            <option value="">Không dùng workflow</option>
            {workflows.filter((workflow) => workflow.isActive).map((workflow) => (
              <option key={workflow.id} value={workflow.id}>
                {workflow.name}{workflow.scope === "department" ? " (Phòng ban)" : ""}
              </option>
            ))}
          </select>
        </div>
        <div className="space-y-2">
          <Label>Delegate specialists (platform agents đã publish)</Label>
          <div className="max-h-48 space-y-1 overflow-auto rounded border p-3">
            {delegates.map((delegate) => (
              <label key={delegate.id} className="flex items-center gap-2 text-sm">
                <input type="checkbox" name="delegateAgentIds" value={delegate.id} defaultChecked={version?.delegateAgentIds?.includes(delegate.id) ?? false} />
                <span>{delegate.name}</span>
                <span className="text-[10px] text-[var(--color-muted-fg)]">{delegate.lifecycleState}</span>
              </label>
            ))}
            {delegates.length === 0 && <p className="text-xs text-[var(--color-muted-fg)]">Chưa có platform agent nào đã publish để làm delegate.</p>}
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

export default async function DepartmentAgentDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ ok?: string; error?: string; tab?: string }>;
}) {
  const { id } = await params;
  const { ok, error, tab } = await searchParams;
  const viewer = await requireViewer(["admin"]);
  const token = viewer.token;

  const [
    detailRes, versionsRes, toolsRes, ownWorkflowsRes, agentsRes,
    ownCapabilitiesRes, ownSkillsRes, deptUsersRes, assignedUsersRes,
    attachedKnowledgeRes, attachableKnowledgeRes,
  ] = await Promise.all([
    gateway.getDepartmentAgent(token, id),
    gateway.listDepartmentAgentVersions(token, id),
    gateway.listTools(token, true),
    // ACL V2-E: same "platform consume-only + own department full ownership"
    // presentation pattern V2-D established for Capabilities — Gateway's
    // EnsureWorkflowEligibleAsync/EnsureSkillsEligibleAsync are what actually
    // enforce eligibility; these extra fetches are presentation only.
    gateway.listOwnDepartmentWorkflows(token),
    gateway.listSystemAgents(token),
    gateway.listOwnDepartmentCapabilities(token),
    gateway.listOwnDepartmentSkills(token),
    gateway.listTenantUsers(token),
    gateway.listDepartmentAgentUsers(token, id),
    gateway.listDepartmentAgentKnowledge(token, id),
    gateway.listAttachableDepartmentAgentKnowledge(token, id),
  ]);

  if (detailRes.status === 403 || detailRes.status === 404) {
    return <p className="text-sm text-red-700">Agent không tồn tại hoặc không thuộc phòng ban của bạn.</p>;
  }

  const deptAgent = (await detailRes.json()) as DepartmentAgentRecord;
  const versions: AgentVersionRecord[] = versionsRes.ok
    ? ((await versionsRes.json()) as { items: AgentVersionRecord[] }).items
    : [];
  const tools: ToolRecord[] = toolsRes.ok ? ((await toolsRes.json()) as { items: ToolRecord[] }).items : [];
  const ownWorkflowsRaw = ownWorkflowsRes.ok
    ? ((await ownWorkflowsRes.json()) as { items: { id: string; name: string; isActive: boolean; publishedVersionId: string | null; scope: "platform" | "department" }[] }).items
    : [];
  // Only published Department Workflows are eligible for attachment
  // (EnsureWorkflowEligibleAsync fails closed on a draft-only workflow).
  const ownWorkflows: WorkflowRecord[] = ownWorkflowsRaw
    .filter((w) => w.scope === "platform" || Boolean(w.publishedVersionId))
    .map((w) => ({ id: w.id, name: w.name, isActive: w.isActive, scope: w.scope }));
  const workflows: WorkflowRecord[] = ownWorkflows;
  const ownSkills: DepartmentSkillRecord[] = ownSkillsRes.ok
    ? ((await ownSkillsRes.json()) as { items: DepartmentSkillRecord[] }).items
    : [];
  const attachedKnowledge: AttachedKnowledgeRecord[] = attachedKnowledgeRes.ok
    ? ((await attachedKnowledgeRes.json()) as { items: AttachedKnowledgeRecord[] }).items
    : [];
  const attachableKnowledge: AttachableKnowledgeRecord[] = attachableKnowledgeRes.ok
    ? ((await attachableKnowledgeRes.json()) as { items: AttachableKnowledgeRecord[] }).items
    : [];
  // Delegate candidates: published, enabled PLATFORM agents only — a
  // Department Agent version's delegate list is only ever accepted by
  // ReplaceCapabilitiesAsync for scope='system' specialist ids (see Gateway),
  // so any Department Agent shown here would silently fail to attach anyway.
  const delegates = agentsRes.ok
    ? ((await agentsRes.json()) as { items: { id: string; name: string; enabled: boolean; lifecycleState: string; publishedVersionId: string | null }[] }).items
        .filter((candidate) => candidate.enabled && candidate.lifecycleState === "production" && Boolean(candidate.publishedVersionId))
        .map((candidate) => ({ id: candidate.id, name: candidate.name, lifecycleState: candidate.lifecycleState }))
    : [];
  const ownCapabilities: CapabilityOption[] = ownCapabilitiesRes.ok
    ? ((await ownCapabilitiesRes.json()) as { items: CapabilityOption[] }).items
    : [];
  const capabilities: CapabilityOption[] = ownCapabilities;
  const departmentUsers: DepartmentUserRecord[] = deptUsersRes.ok
    ? ((await deptUsersRes.json()) as { items: DepartmentUserRecord[] }).items
    : [];
  const assignedUsers: AgentUserAssignmentRecord[] = assignedUsersRes.ok
    ? ((await assignedUsersRes.json()) as { items: AgentUserAssignmentRecord[] }).items
    : [];
  const assignedUserIds = new Set(assignedUsers.map((u) => u.userId));

  const draftVersion = deptAgent.currentDraftVersionId
    ? versions.find((v) => v.id === deptAgent.currentDraftVersionId) ?? null
    : null;
  const publishedVersion = deptAgent.publishedVersionId
    ? versions.find((v) => v.id === deptAgent.publishedVersionId) ?? null
    : null;
  const testableLabel = draftVersion
    ? `Đang test bản draft v${draftVersion.version} (chưa publish)`
    : publishedVersion
    ? `Đang test bản đã publish v${publishedVersion.version}`
    : null;
  const activeVersion = draftVersion ?? publishedVersion;
  const draftAction = draftVersion ? updateDraftAction : createDraftAction;

  // Configurable document processing of the version being edited (draft,
  // else the read-only published one) + the SYSTEM type catalog + samples
  // for Test mode (this department's own vouchers that already have OCR text).
  const [docTypesRes, docConfigRes, samplesRes] = await Promise.all([
    gateway.listDepartmentDocumentTypes(token),
    activeVersion ? gateway.getDepartmentAgentDocumentProcessing(token, id, activeVersion.id) : Promise.resolve(null),
    gateway.listVouchers(token, { limit: 30 }),
  ]);
  const docCatalog = docTypesRes.ok
    ? ((await docTypesRes.json()) as { items: CatalogDocumentType[]; fieldTypes: string[] })
    : { items: [] as CatalogDocumentType[], fieldTypes: ["string", "decimal", "integer", "date", "boolean", "array", "object"] };
  const docConfig: DocumentTypeConfig[] = docConfigRes?.ok
    ? ((await docConfigRes.json()) as { types: DocumentTypeConfig[] }).types
    : [];
  const sampleVouchers = samplesRes.ok
    ? ((await samplesRes.json()) as { items?: { id: string; voucherType: string; documentNumber: string | null; sourceFileName: string | null; status: string }[] }).items ?? []
    : [];

  const TABS = [
    { value: "identity", label: "Identity" },
    { value: "runtime", label: "Runtime" },
    { value: "capabilities", label: "Nghiệp vụ" },
    { value: "workflow", label: "Workflow / Delegates" },
    { value: "documents", label: "Xử lý chứng từ" },
    { value: "knowledge", label: `Knowledge (${attachedKnowledge.length})` },
    { value: "users", label: `Nhân sự (${assignedUsers.length})` },
    { value: "versions", label: `Version History (${versions.length})` },
    { value: "test", label: "Test" },
  ] as const;
  const defaultTab = TABS.some((t) => t.value === tab) ? (tab as string) : "identity";

  return (
    <div className="space-y-6">
      <Link
        href="/department/agents"
        className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
      >
        <ArrowLeft className="size-3.5" /> Tất cả agent phòng ban
      </Link>

      <header className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-serif text-3xl">{deptAgent.name}</h1>
          {deptAgent.lifecycleState === "production" && (
            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">
              <ShieldCheck className="size-3" /> production
            </span>
          )}
          {!deptAgent.enabled && (
            <span className="inline-flex items-center gap-1 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700">
              <Power className="size-3" /> disabled
            </span>
          )}
        </div>
        <p className="font-mono text-xs text-[var(--color-muted-fg)]">{deptAgent.slug}</p>
        {deptAgent.description && <p className="text-sm">{deptAgent.description}</p>}
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
              <input type="hidden" name="agentId" value={deptAgent.id} />
              <div className="space-y-1.5">
                <Label htmlFor="i-name">Tên</Label>
                <Input id="i-name" name="name" defaultValue={deptAgent.name} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="i-desc">Mô tả</Label>
                <textarea id="i-desc" name="description" rows={2} defaultValue={deptAgent.description ?? ""} className={textareaClass} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="i-tier">Action Tier</Label>
                <select id="i-tier" name="actionTier" defaultValue={deptAgent.actionTier} className={selectClass}>
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

          <section className={`space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 ${!deptAgent.enabled ? "border-red-300" : ""}`}>
            <h2 className="font-serif text-lg">Kill Switch</h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Disable dừng agent ngay với mọi user trong phòng ban, không cần rollback version.
            </p>
            <form action={toggleEnabledAction}>
              <input type="hidden" name="agentId" value={deptAgent.id} />
              <input type="hidden" name="action" value={deptAgent.enabled ? "disable" : "enable"} />
              <Button type="submit" size="sm" variant="outline" className="w-full">
                <Power className="size-3.5" /> {deptAgent.enabled ? "Disable" : "Enable lại"}
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
          <DraftForm agentId={deptAgent.id} version={activeVersion} tools={tools} ownSkills={ownSkills} capabilities={capabilities} workflows={workflows} delegates={delegates} formAction={draftAction} />
        </div>

        {draftVersion && (
          <>
            <form id="validate-draft-form" action={validateDraftAction} className="hidden">
              <input type="hidden" name="agentId" value={deptAgent.id} />
              <input type="hidden" name="versionId" value={draftVersion.id} />
            </form>
            <form id="publish-draft-form" action={publishDraftAction} className="hidden">
              <input type="hidden" name="agentId" value={deptAgent.id} />
              <input type="hidden" name="versionId" value={draftVersion.id} />
            </form>
          </>
        )}

        <TabsContent value="documents">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">Xử lý chứng từ</h2>
            <DocumentProcessingEditor
              key={activeVersion?.id ?? "none"}
              agentId={deptAgent.id}
              version={activeVersion ? { id: activeVersion.id, version: activeVersion.version, status: activeVersion.status } : null}
              initialTypes={docConfig}
              catalog={docCatalog.items}
              fieldTypes={docCatalog.fieldTypes}
              workflows={workflows.filter((w) => w.isActive).map((w) => ({ id: w.id, name: w.name, scope: w.scope }))}
              skills={ownSkills.filter((s) => s.isActive).map((s) => ({ code: s.code, name: s.name, scope: s.scope }))}
              sampleVouchers={sampleVouchers
                .filter((v) => v.status !== "uploading" && v.status !== "ocr_unavailable")
                .map((v) => ({ id: v.id, label: `${v.documentNumber ?? v.sourceFileName ?? v.id.slice(0, 8)} · ${v.voucherType}` }))}
            />
          </section>
        </TabsContent>

        <TabsContent value="knowledge">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">Knowledge đã gắn</h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Chọn Knowledge Hệ thống hoặc Knowledge của phòng ban từ danh sách hợp nhất tại{" "}
              <Link href="/department/knowledge" className="underline">Knowledge</Link>.
            </p>
            {attachedKnowledge.length === 0 ? (
              <p className="text-sm text-[var(--color-muted-fg)]">Chưa gắn Knowledge nào.</p>
            ) : (
              <ul className="space-y-1.5">
                {attachedKnowledge.map((n) => (
                  <li key={n.id} className="flex items-center justify-between gap-2 rounded border p-2 text-sm">
                    <span>{n.title} <small className="text-[var(--color-muted-fg)]">· {n.scope === "department" ? "Phòng ban" : "Hệ thống"}</small></span>
                    <form action={detachKnowledgeAction}>
                      <input type="hidden" name="agentId" value={deptAgent.id} />
                      <input type="hidden" name="noteId" value={n.id} />
                      <Button type="submit" size="sm" variant="ghost">Gỡ</Button>
                    </form>
                  </li>
                ))}
              </ul>
            )}
            {attachableKnowledge.length > 0 && (
              <form action={attachKnowledgeAction} className="flex items-center gap-2 border-t pt-3">
                <input type="hidden" name="agentId" value={deptAgent.id} />
                <select name="noteId" required className={`${selectClass} sm:w-64`}>
                  <option value="" disabled>— Chọn Knowledge để gắn —</option>
                  {attachableKnowledge.map((n) => (
                    <option key={n.id} value={n.id}>{n.title} · {n.scope === "department" ? "Phòng ban" : "Hệ thống"}</option>
                  ))}
                </select>
                <Button type="submit" size="sm">Gắn</Button>
              </form>
            )}
          </section>
        </TabsContent>

        <TabsContent value="users">
          <section className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
            <h2 className="font-serif text-lg">
              <Users className="mr-2 inline size-4" /> Nhân sự
            </h2>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Bạn (ADMIN) luôn dùng được agent này. USER trong phòng ban chỉ dùng được sau khi được gán ở đây.
              {!publishedVersion && " Agent chưa publish version nào — chưa ai dùng được cho tới khi publish."}
            </p>
            {departmentUsers.length === 0 ? (
              <p className="text-sm text-[var(--color-muted-fg)]">Phòng ban chưa có USER nào.</p>
            ) : (
              <div className="overflow-hidden rounded border">
                <table className="w-full text-sm">
                  <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                    <tr>
                      <th className="px-3 py-2 text-left">Nhân sự</th>
                      <th className="px-3 py-2 text-left">Trạng thái</th>
                      <th className="px-3 py-2 text-right">—</th>
                    </tr>
                  </thead>
                  <tbody>
                    {departmentUsers.map((u) => {
                      const assigned = assignedUserIds.has(u.id);
                      return (
                        <tr key={u.id} className="border-t align-top">
                          <td className="px-3 py-2">
                            <div className="font-medium">{u.displayName ?? u.email}</div>
                            <div className="text-[10px] text-[var(--color-muted-fg)]">{u.email}</div>
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
                              <form action={revokeUserAction}>
                                <input type="hidden" name="agentId" value={deptAgent.id} />
                                <input type="hidden" name="userId" value={u.id} />
                                <Button type="submit" size="sm" variant="ghost">
                                  Revoke
                                </Button>
                              </form>
                            ) : (
                              <form action={assignUserAction}>
                                <input type="hidden" name="agentId" value={deptAgent.id} />
                                <input type="hidden" name="userId" value={u.id} />
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
                  {testableLabel}. Chạy qua đúng pipeline runtime thật (provider/model/prompt/tools/skills từ database) —
                  chỉ ADMIN của phòng ban sở hữu agent này gọi được, phòng ban khác bị từ chối kể cả biết đúng agent id.
                </p>
                <AgentTestPanel agentId={deptAgent.id} testEndpoint={`/api/department-agents/${deptAgent.id}/test`} />
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
