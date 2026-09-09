import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { CheckCircle2, GitBranch, Plus, Power, ShieldCheck } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { WorkflowStepEditor } from "@/components/workflow-step-editor";

// Platform (Global) Workflow catalog — SUPER_ADMIN CRUD. Uses the SAME
// structured step editor (WorkflowStepEditor) as ADMIN's Department
// Workflow page (/department/workflows) — same editor UX everywhere;
// only the backend scope/authorization differs (platform_admin here,
// department_admin + own-tenant/department there). Platform workflows have
// no separate draft/published version lifecycle (unlike Department
// Workflows) — is_active is the platform equivalent of "published".

interface Workflow {
  id: string;
  name: string;
  description: string | null;
  definitionJson: string;
  isActive: boolean;
}

function errorRedirect(message: string): never {
  redirect(`/admin/workflows?error=${encodeURIComponent(message.slice(0, 200))}`);
}

async function createWorkflowAction(formData: FormData): Promise<void> {
  "use server";
  const name = String(formData.get("name") ?? "").trim();
  if (!name) errorRedirect("name-required");
  const definition = JSON.parse(String(formData.get("definition") ?? "{}"));
  if (!Array.isArray(definition.steps) || definition.steps.length === 0) {
    errorRedirect("workflow_has_no_steps");
  }

  const viewer = await requireViewer(["super_admin"]);
  const res = await gateway.createWorkflow(viewer.token, {
    name,
    description: String(formData.get("description") ?? "").trim() || undefined,
    definition,
  });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/admin/workflows");
  redirect("/admin/workflows?ok=created");
}

async function updateWorkflowAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  const name = String(formData.get("name") ?? "").trim();
  if (!workflowId || !name) errorRedirect("invalid");
  const definition = JSON.parse(String(formData.get("definition") ?? "{}"));
  if (!Array.isArray(definition.steps) || definition.steps.length === 0) {
    errorRedirect("workflow_has_no_steps");
  }

  const viewer = await requireViewer(["super_admin"]);
  const res = await gateway.updateWorkflow(viewer.token, workflowId, {
    name,
    description: String(formData.get("description") ?? "").trim() || undefined,
    definition,
  });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/admin/workflows");
  redirect("/admin/workflows?ok=saved");
}

async function toggleActiveAction(formData: FormData): Promise<void> {
  "use server";
  const workflowId = String(formData.get("workflowId") ?? "");
  const isActive = formData.get("isActive") === "true";
  if (!workflowId) errorRedirect("invalid");

  const viewer = await requireViewer(["super_admin"]);
  const res = await gateway.updateWorkflow(viewer.token, workflowId, { isActive: !isActive });
  if (!res.ok) errorRedirect("toggle-failed");
  revalidatePath("/admin/workflows");
  redirect(`/admin/workflows?ok=${isActive ? "disabled" : "enabled"}`);
}

export default async function WorkflowsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const viewer = await requireViewer(["super_admin"]);
  const response = await gateway.listWorkflows(viewer.token);
  const items: Workflow[] = response.ok ? ((await response.json()) as { items: Workflow[] }).items : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Workflows — Hệ thống</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Danh mục workflow nền tảng dùng chung cho toàn bộ phòng ban. ADMIN chỉ xem/chọn được;
          chỉnh sửa nội dung workflow nền tảng chỉ thuộc quyền quản trị nền tảng.
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
        <form action={createWorkflowAction} className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="new-name">Tên</Label>
              <Input id="new-name" name="name" required placeholder="Duyệt hoá đơn" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="new-desc">Mô tả (tuỳ chọn)</Label>
              <Input id="new-desc" name="description" />
            </div>
          </div>
          <WorkflowStepEditor />
          <Button type="submit" size="sm">Tạo</Button>
        </form>
      </section>

      <section className="space-y-4">
        <h2 className="font-serif text-lg">Workflow nền tảng ({items.length})</h2>
        {items.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Chưa có workflow nền tảng nào.
          </div>
        ) : (
          items.map((workflow) => (
            <details key={workflow.id} className="rounded-[var(--radius)] border p-4">
              <summary className="flex cursor-pointer items-center justify-between gap-4">
                <div className="flex items-center gap-2 font-medium">
                  <GitBranch className="size-4" /> {workflow.name}
                </div>
                {workflow.isActive ? (
                  <span className="inline-flex items-center gap-1 text-xs text-emerald-700">
                    <ShieldCheck className="size-3" /> hoạt động
                  </span>
                ) : (
                  <span className="inline-flex items-center gap-1 text-xs text-red-700">
                    <Power className="size-3" /> tắt
                  </span>
                )}
              </summary>
              <div className="mt-3 space-y-4 border-t pt-3">
                {workflow.description && <p className="text-xs text-[var(--color-muted-fg)]">{workflow.description}</p>}
                <form action={updateWorkflowAction} className="space-y-4">
                  <input type="hidden" name="workflowId" value={workflow.id} />
                  <div className="grid gap-3 sm:grid-cols-2">
                    <div className="space-y-1.5">
                      <Label htmlFor={`name-${workflow.id}`}>Tên</Label>
                      <Input id={`name-${workflow.id}`} name="name" required defaultValue={workflow.name} />
                    </div>
                    <div className="space-y-1.5">
                      <Label htmlFor={`desc-${workflow.id}`}>Mô tả</Label>
                      <Input id={`desc-${workflow.id}`} name="description" defaultValue={workflow.description ?? ""} />
                    </div>
                  </div>
                  <WorkflowStepEditor initialDefinitionJson={workflow.definitionJson} />
                  <Button type="submit" size="sm">Lưu</Button>
                </form>
                <form action={toggleActiveAction}>
                  <input type="hidden" name="workflowId" value={workflow.id} />
                  <input type="hidden" name="isActive" value={String(workflow.isActive)} />
                  <Button type="submit" size="sm" variant="outline">{workflow.isActive ? "Disable" : "Enable"}</Button>
                </form>
              </div>
            </details>
          ))
        )}
      </section>
    </div>
  );
}
