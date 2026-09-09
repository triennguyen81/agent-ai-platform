import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { Archive, BookOpen, CheckCircle2, Plus, Trash2 } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";

// ACL V2-E: ADMIN's own department Knowledge ownership — plain text/markdown
// content, reusing the Notes feature's `notes` table with scope='department'
// (distinct from the personal /knowledge page's per-user notes). Attaching a
// Knowledge item to a specific Department Agent draft happens on that
// Agent's own Builder page, not here.

interface KnowledgeRecord {
  id: string;
  title: string;
  content: string;
  tags: string[];
  isArchived: boolean;
  scope: "platform" | "department";
}

function errorRedirect(message: string): never {
  redirect(`/department/knowledge?error=${encodeURIComponent(message.slice(0, 200))}`);
}

async function createKnowledgeAction(formData: FormData): Promise<void> {
  "use server";
  const title = String(formData.get("title") ?? "").trim();
  if (!title) errorRedirect("title-required");

  const viewer = await requireViewer(["admin"]);
  const tags = String(formData.get("tags") ?? "")
    .split(",")
    .map((t) => t.trim())
    .filter(Boolean);
  const res = await gateway.createDepartmentKnowledge(viewer.token, {
    title,
    content: String(formData.get("content") ?? ""),
    tags,
  });
  if (!res.ok) errorRedirect(await res.text());
  revalidatePath("/department/knowledge");
  redirect("/department/knowledge?ok=created");
}

async function archiveAction(formData: FormData): Promise<void> {
  "use server";
  const noteId = String(formData.get("noteId") ?? "");
  if (!noteId) errorRedirect("invalid");
  const viewer = await requireViewer(["admin"]);
  const res = await gateway.archiveDepartmentKnowledge(viewer.token, noteId);
  if (!res.ok) errorRedirect("archive-failed");
  revalidatePath("/department/knowledge");
  redirect("/department/knowledge?ok=archived");
}

async function deleteAction(formData: FormData): Promise<void> {
  "use server";
  const noteId = String(formData.get("noteId") ?? "");
  if (!noteId) errorRedirect("invalid");
  const viewer = await requireViewer(["admin"]);
  const res = await gateway.deleteDepartmentKnowledge(viewer.token, noteId);
  if (!res.ok) {
    if (res.status === 409) errorRedirect("attached-to-an-agent-detach-first-or-archive-instead");
    errorRedirect("delete-failed");
  }
  revalidatePath("/department/knowledge");
  redirect("/department/knowledge?ok=deleted");
}

export default async function DepartmentKnowledgePage({
  searchParams,
}: {
  searchParams: Promise<{ ok?: string; error?: string }>;
}) {
  const { ok, error } = await searchParams;
  const viewer = await requireViewer(["admin"]);

  const listRes = await gateway.listOwnDepartmentKnowledge(viewer.token);
  const items: KnowledgeRecord[] = listRes.ok ? ((await listRes.json()) as { items: KnowledgeRecord[] }).items : [];

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Knowledge</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Knowledge Hệ thống và Knowledge của phòng ban được hiển thị chung. Nội dung Hệ thống chỉ
          đọc/chọn; nội dung phòng ban do chính phòng ban quản lý.
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
          <Plus className="size-4" /> Thêm tài liệu mới
        </h2>
        <form action={createKnowledgeAction} className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="k-title">Tiêu đề</Label>
            <Input id="k-title" name="title" required placeholder="Bảng hệ thống tài khoản" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="k-tags">Thẻ (phân cách bằng dấu phẩy)</Label>
            <Input id="k-tags" name="tags" placeholder="ke-toan, huong-dan" />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="k-content">Nội dung</Label>
            <Textarea id="k-content" name="content" rows={6} />
          </div>
          <div className="sm:col-span-2">
            <Button type="submit" size="sm">Tạo</Button>
          </div>
        </form>
      </section>

      <section className="space-y-3">
        <h2 className="font-serif text-lg">Knowledge khả dụng ({items.length})</h2>
        {items.length === 0 ? (
          <div className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            Phòng ban bạn chưa có tài liệu Knowledge riêng nào.
          </div>
        ) : (
          items.map((n) => (
            <div key={n.id} className="space-y-2 rounded-[var(--radius)] border p-4">
              <div className="flex items-start justify-between gap-4">
                <div>
                  <div className="flex items-center gap-2 font-medium">
                    <BookOpen className="size-4" /> {n.title}
                  </div>
                  <div className="mt-1"><ResourceScopeBadge scope={n.scope} /></div>
                  {n.tags.length > 0 && (
                    <div className="mt-1 flex flex-wrap gap-1">
                      {n.tags.map((t) => (
                        <span key={t} className="rounded-full bg-[var(--color-muted)] px-2 py-0.5 text-[10px]">{t}</span>
                      ))}
                    </div>
                  )}
                  <p className="mt-1 line-clamp-2 text-xs text-[var(--color-muted-fg)]">{n.content}</p>
                </div>
                {n.scope === "department" && <div className="flex shrink-0 gap-2">
                  <form action={archiveAction}>
                    <input type="hidden" name="noteId" value={n.id} />
                    <Button type="submit" size="sm" variant="outline" title="Lưu trữ">
                      <Archive className="size-3.5" />
                    </Button>
                  </form>
                  <form action={deleteAction}>
                    <input type="hidden" name="noteId" value={n.id} />
                    <Button type="submit" size="sm" variant="ghost" title="Xoá">
                      <Trash2 className="size-3.5" />
                    </Button>
                  </form>
                </div>}
              </div>
            </div>
          ))
        )}
      </section>
    </div>
  );
}
