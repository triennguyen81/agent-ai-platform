import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { CheckCircle2, GitBranch, Plus, Power, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";
import { ReadOnlyWorkflowSteps, WorkflowStepEditor } from "@/components/workflow-step-editor";

// ACL V2-E: ADMIN's own department Workflow ownership — draft/publish
// versioning mirrors the Department Agent Builder's own version lifecycle.
// A published version's definition is immutable (see migration 026); to
// change behavior, ADMIN creates a new draft (from the "New draft" button
// once no draft is pending) and publishes it as the next version.

interface WorkflowRecord {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  currentDraftVersionId: string | null;
  publishedVersionId: string | null;
  scope: "platform" | "department";
}

interface WorkflowVersionRecord {
  id: string;
  workflowId: string;
  version: number;
  status: "draft" | "published";
  definitionJson: string;
}

function errorRedirect(message: string): never {
  redirect(`/department/workflows?error=${encodeURIComponent(message.slice(0, 200))}`);
}

async function createWorkflowAction(formData: FormData): Promise<void> {
  "use server";
  const name = String(formData.get("name") ?? "").trim();
  if (!name) errorRedirect("name-required");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentWorkflow(viewer.token, {
    name,
    description: String(formData.get("description") ?? "").trim() || undefined,
  });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/workflows");
  redirect("/department/workflows?ok=created");
}

async function toggleEnabledAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!workflowId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res =
    action === "enable"
      ? await gateway.enableDepartmentWorkflow(viewer.token, workflowId)
      : await gateway.disableDepartmentWorkflow(viewer.token, workflowId);
  if (!res.ok) errorRedirect("toggle-failed");
  revalidatePath("/department/workflows");
  redirect(`/department/workflows?ok=${action}d`);
}

async function updateDraftDefinitionAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!workflowId || !versionId) errorRedirect("invalid");
  const definition = JSON.parse(String(formData.get("definition") ?? "{}"));

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.updateDepartmentWorkflowDraftVersion(viewer.token, workflowId, versionId, { definition });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/workflows");
  redirect("/department/workflows?ok=draft-updated");
}

async function publishVersionAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  const versionId = String(formData.get("versionId") ?? "");
  if (!workflowId || !versionId) errorRedirect("invalid");
  const definitionRaw = formData.get("definition");

  const viewer = await requireViewer(["admin"]);
  // The Publish button shares its form with the step editor — save whatever
  // is currently in the editor first, so publishing can never silently
  // discard an unsaved edit (what the ADMIN sees is what gets published).
  if (typeof definitionRaw === "string" && definitionRaw) {
    const saveRes = await gateway.updateDepartmentWorkflowDraftVersion(viewer.token, workflowId, versionId, {
      definition: JSON.parse(definitionRaw),
    });
    if (!saveRes.ok) errorRedirect(await saveRes.text());
  }
  const res = await gateway.publishDepartmentWorkflowVersion(viewer.token, workflowId, versionId);
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/workflows");
  redirect("/department/workflows?ok=published");
}

async function newDraftVersionAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  if (!workflowId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentWorkflowDraftVersion(viewer.token, workflowId, {});
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/workflows");
  redirect("/department/workflows?ok=draft-created");
}

export default async function DepartmentWorkflowsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const viewer = await requireViewer(["admin"]);

  const listRes = await gateway.listOwnDepartmentWorkflows(viewer.token);
  const workflows: WorkflowRecord[] = listRes.ok
    ? ((await listRes.json()) as { items: WorkflowRecord[] }).items
    : [];

  const versionsByWorkflow = new Map<string, WorkflowVersionRecord[]>();
  await Promise.all(
    workflows.filter((w) => w.scope === "department").map(async (w) => {
      const res = await gateway.listDepartmentWorkflowVersions(viewer.token, w.id);
      if (res.ok) {
        const { items } = (await res.json()) as { items: WorkflowVersionRecord[] };
        versionsByWorkflow.set(w.id, items);
      }
    }),
  );

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Workflows</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Workflow Hệ thống và Workflow của phòng ban được hiển thị chung. Workflow Hệ thống chỉ
          đọc/chọn; phiên bản phòng ban đã công bố luôn bất biến.
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
          <Plus className="size-4" /> Tạo workflow mới
        </h2>
        <form action={createWorkflowAction} className="grid gap-3 sm:grid-cols-3">
          <div className="space-y-1.5">
            <Label htmlFor="w-name">Tên</Label>
            <Input id="w-name" name="name" required placeholder="Duyệt hoá đơn" />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="w-desc">Mô tả (tuỳ chọn)</Label>
            <Input id="w-desc" name="description" />
          </div>
          <div className="sm:col-span-3">
            <Button type="submit" size="sm">Tạo</Button>
          </div>
        </form>
      </section>

      <section className="space-y-4">
        <h2 className="font-serif text-lg">Workflow khả dụng ({workflows.length})</h2>
        {workflows.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Phòng ban bạn chưa tạo workflow riêng nào.
          </div>
        ) : (
          workflows.map((w) => {
            const versions = (versionsByWorkflow.get(w.id) ?? []).sort((a, b) => b.version - a.version);
            const draft = versions.find((v) => v.status === "draft");
            return (
              <div key={w.id} className="space-y-3 rounded-[var(--radius)] border p-4">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <div className="flex items-center gap-2 font-medium">
                      <GitBranch className="size-4" /> {w.name}
                    </div>
                    <div className="mt-1"><ResourceScopeBadge scope={w.scope} /></div>
                    {w.description && <p className="text-xs text-[var(--color-muted-fg)]">{w.description}</p>}
                  </div>
                  <div className="flex items-center gap-2">
                    {w.isActive ? (
                      <span className="inline-flex items-center gap-1 text-xs text-emerald-700">
                        <ShieldCheck className="size-3" /> hoạt động
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 text-xs text-red-700">
                        <Power className="size-3" /> tắt
                      </span>
                    )}
                    {w.scope === "department" ? <form action={toggleEnabledAction}>
                      <input type="hidden" name="workflowId" value={w.id} />
                      <input type="hidden" name="action" value={w.isActive ? "disable" : "enable"} />
                      <Button type="submit" size="sm" variant="outline">{w.isActive ? "Disable" : "Enable"}</Button>
                    </form> : <span className="text-xs text-[var(--color-muted-fg)]">Chỉ đọc</span>}
                  </div>
                </div>

                {w.scope === "department" && <div className="space-y-3 border-t pt-3">
                  {versions.map((v) => (
                    <div key={v.id} className="rounded border p-3 text-xs">
                      <div className="mb-2 flex items-center gap-2">
                        <span className="font-mono">v{v.version}</span>
                        <span className={v.status === "published" ? "text-emerald-700" : "text-amber-700"}>
                          {v.status === "published" ? "đã công bố" : "nháp"}
                        </span>
                      </div>
                      {v.status === "draft" ? (
                        <form action={updateDraftDefinitionAction} className="space-y-3">
                          <input type="hidden" name="workflowId" value={w.id} />
                          <input type="hidden" name="versionId" value={v.id} />
                          <WorkflowStepEditor initialDefinitionJson={v.definitionJson} />
                          <div className="flex gap-2">
                            <Button type="submit" size="sm" variant="outline">Lưu nháp</Button>
                            <Button type="submit" formAction={publishVersionAction} size="sm">Công bố</Button>
                          </div>
                        </form>
                      ) : (
                        <ReadOnlyWorkflowSteps definitionJson={v.definitionJson} />
                      )}
                    </div>
                  ))}
                  {!draft && (
                    <form action={newDraftVersionAction}>
                      <input type="hidden" name="workflowId" value={w.id} />
                      <Button type="submit" size="sm" variant="outline">+ Bản nháp mới</Button>
                    </form>
                  )}
                </div>}
              </div>
            );
          })
        )}
      </section>
    </div>
  );
}
