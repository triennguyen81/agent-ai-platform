import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { CheckCircle2, Plus, Power, ShieldCheck, Sparkles } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";
import { Input, Label, Textarea } from "@/components/ui/input";

// ACL V2-E: ADMIN's own department Skill ownership. Skills are declarative
// instruction text only — never executable code. Once a skill's code is
// referenced by a PUBLISHED Department Agent version, its content locks
// (edits return 409); metadata (name/description) stays editable.

interface SkillRecord {
  id: string;
  code: string;
  name: string;
  description: string | null;
  scope: "platform" | "department";
  isActive: boolean;
  declarativeContent: string | null;
}

function errorRedirect(message: string): never {
  redirect(`/department/skills?error=${encodeURIComponent(message.slice(0, 200))}`);
}

async function createSkillAction(formData: FormData): Promise<void> {
  "use server";
  const code = String(formData.get("code") ?? "").trim();
  const name = String(formData.get("name") ?? "").trim();
  const declarativeContent = String(formData.get("declarativeContent") ?? "").trim();
  if (!code || !name || !declarativeContent) errorRedirect("code-name-and-content-required");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.createDepartmentSkill(viewer.token, {
    code,
    name,
    description: String(formData.get("description") ?? "").trim() || undefined,
    declarativeContent,
  });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/skills");
  redirect("/department/skills?ok=created");
}

async function updateContentAction(formData: FormData): Promise<void> {
  "use server";
  const skillId = String(formData.get("skillId") ?? "");
  const declarativeContent = String(formData.get("declarativeContent") ?? "").trim();
  if (!skillId || !declarativeContent) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res = await gateway.updateDepartmentSkillContent(viewer.token, skillId, { declarativeContent });
  if (!res.ok) {
    if (res.status === 409) errorRedirect("content-locked-by-published-agent-version");
    errorRedirect(await res.text());
  }
  revalidatePath("/department/skills");
  redirect("/department/skills?ok=content-updated");
}

async function toggleEnabledAction(formData: FormData): Promise<void> {
  "use server";
  const skillId = String(formData.get("skillId") ?? "");
  const action = String(formData.get("action") ?? "");
  if (!skillId) errorRedirect("invalid");

  const viewer = await requireViewer(["admin"]);
  const res =
    action === "enable"
      ? await gateway.enableDepartmentSkill(viewer.token, skillId)
      : await gateway.disableDepartmentSkill(viewer.token, skillId);
  if (!res.ok) errorRedirect("toggle-failed");
  revalidatePath("/department/skills");
  redirect(`/department/skills?ok=${action}d`);
}

export default async function DepartmentSkillsPage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const viewer = await requireViewer(["admin"]);

  const listRes = await gateway.listOwnDepartmentSkills(viewer.token);
  const skills: SkillRecord[] = listRes.ok ? ((await listRes.json()) as { items: SkillRecord[] }).items : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Skills</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Skill Hệ thống và Skill của phòng ban được hiển thị chung. Skill Hệ thống chỉ đọc/chọn;
          phòng ban quản lý toàn bộ vòng đời Skill do mình tạo.
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
          <Plus className="size-4" /> Tạo skill mới
        </h2>
        <p className="text-xs text-[var(--color-muted-fg)]">
          Mã (code) dùng chữ thường/số, nối bằng dấu gạch ngang — VD: <code className="font-mono">invoice-review</code>.
        </p>
        <form action={createSkillAction} className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="s-code">Mã (code)</Label>
            <Input id="s-code" name="code" required placeholder="invoice-review" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="s-name">Tên</Label>
            <Input id="s-name" name="name" required placeholder="Kiểm tra hoá đơn" />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="s-desc">Mô tả (tuỳ chọn)</Label>
            <Input id="s-desc" name="description" />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="s-content">Nội dung hướng dẫn</Label>
            <Textarea id="s-content" name="declarativeContent" required rows={4} />
          </div>
          <div className="sm:col-span-2">
            <Button type="submit" size="sm">Tạo</Button>
          </div>
        </form>
      </section>

      <section className="space-y-3">
        <h2 className="font-serif text-lg">Skill khả dụng ({skills.length})</h2>
        {skills.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Phòng ban bạn chưa tạo skill riêng nào.
          </div>
        ) : (
          skills.map((s) => (
            <div key={s.id} className="space-y-2 rounded-[var(--radius)] border p-4">
              <div className="flex items-start justify-between gap-4">
                <div>
                  <div className="flex items-center gap-2 font-medium">
                    <Sparkles className="size-4" /> {s.name}
                  </div>
                  <div className="font-mono text-[10px] text-[var(--color-muted-fg)]">{s.code}</div>
                  <div className="mt-1"><ResourceScopeBadge scope={s.scope} /></div>
                  {s.description && <p className="text-xs text-[var(--color-muted-fg)]">{s.description}</p>}
                </div>
                <div className="flex items-center gap-2">
                  {s.isActive ? (
                    <span className="inline-flex items-center gap-1 text-xs text-emerald-700">
                      <ShieldCheck className="size-3" /> hoạt động
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-1 text-xs text-red-700">
                      <Power className="size-3" /> tắt
                    </span>
                  )}
                  {s.scope === "department" ? <form action={toggleEnabledAction}>
                    <input type="hidden" name="skillId" value={s.id} />
                    <input type="hidden" name="action" value={s.isActive ? "disable" : "enable"} />
                    <Button type="submit" size="sm" variant="outline">{s.isActive ? "Disable" : "Enable"}</Button>
                  </form> : <span className="text-xs text-[var(--color-muted-fg)]">Chỉ đọc</span>}
                </div>
              </div>
              {s.scope === "department" && <form action={updateContentAction} className="flex items-start gap-2">
                <input type="hidden" name="skillId" value={s.id} />
                <Textarea name="declarativeContent" defaultValue={s.declarativeContent ?? ""} rows={3} className="flex-1 text-xs" />
                <Button type="submit" size="sm" variant="outline">Lưu</Button>
              </form>}
            </div>
          ))
        )}
      </section>
    </div>
  );
}
