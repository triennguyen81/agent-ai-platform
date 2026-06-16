import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { clearSession, requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";
import { BulkMarkdownUpload } from "@/components/bulk-md-upload";
import { TextareaWithMic } from "@/components/textarea-with-mic";

type NoteRecord = {
  id: string;
  title: string;
  content: string;
  tags: string[];
  updated_at: string;
};

async function createNoteAction(formData: FormData) {
  "use server";
  const token = await requireSession();
  const title = String(formData.get("title") ?? "").trim();
  const content = String(formData.get("content") ?? "");
  const tags = String(formData.get("tags") ?? "")
    .split(",")
    .map((t) => t.trim())
    .filter(Boolean);
  if (!title) return;
  const res = await gateway.createNote(token, { title, content, tags });
  if (!res.ok) return;
  revalidatePath("/knowledge");
}

async function deleteNoteAction(formData: FormData) {
  "use server";
  const token = await requireSession();
  const id = String(formData.get("id") ?? "");
  if (!id) return;
  await gateway.deleteNote(token, id);
  revalidatePath("/knowledge");
}

function formatDate(s: string): string {
  try {
    return new Date(s).toLocaleString("vi-VN", {
      dateStyle: "medium",
      timeStyle: "short",
    });
  } catch {
    return s;
  }
}

export default async function KnowledgePage() {
  const token = await requireSession();

  // 3-tier gate: only admin or super_admin can manage knowledge.
  // Plain `user` accounts get redirected to /chat (they can still consume
  // KB via chat, just not edit it).
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    const roles = me.roles ?? [];
    if (!roles.includes("super_admin") && !roles.includes("admin")) {
      redirect("/chat");
    }
  }

  const res = await gateway.listNotes(token);
  if (res.status === 401) {
    await clearSession();
    redirect("/login?error=session-expired");
  }
  const body = res.ok
    ? ((await res.json()) as { items: NoteRecord[] })
    : { items: [] };

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_360px]">
      <section className="space-y-4">
        <header>
          <h1 className="font-serif text-3xl">Your knowledge</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            Notes, snippets, anything you want the assistant to know.
          </p>
        </header>

        {body.items.length === 0 ? (
          <p className="rounded-[var(--radius)] border border-dashed p-8 text-center text-sm text-[var(--color-muted-fg)]">
            No notes yet. Add your first one on the right →
          </p>
        ) : (
          <ul className="space-y-3">
            {body.items.map((n) => (
              <li
                key={n.id}
                className="rounded-[var(--radius)] border bg-[var(--color-muted)] p-4"
              >
                <header className="flex items-baseline justify-between gap-4">
                  <h2 className="font-serif text-lg">{n.title}</h2>
                  <span className="shrink-0 text-xs text-[var(--color-muted-fg)]">
                    {formatDate(n.updated_at)}
                  </span>
                </header>
                {n.content && (
                  <p className="mt-1 line-clamp-3 whitespace-pre-wrap text-sm text-[var(--color-muted-fg)]">
                    {n.content}
                  </p>
                )}
                {n.tags?.length > 0 && (
                  <div className="mt-2 flex flex-wrap gap-1">
                    {n.tags.map((t) => (
                      <span
                        key={t}
                        className="rounded-full border px-2 py-0.5 text-xs text-[var(--color-muted-fg)]"
                      >
                        {t}
                      </span>
                    ))}
                  </div>
                )}
                <form action={deleteNoteAction} className="mt-3">
                  <input type="hidden" name="id" value={n.id} />
                  <Button variant="ghost" size="sm" type="submit">
                    Delete
                  </Button>
                </form>
              </li>
            ))}
          </ul>
        )}
      </section>

      <aside className="space-y-4">
        <BulkMarkdownUpload />

        <h2 className="font-serif text-xl">Add a note</h2>
        <form action={createNoteAction} className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="title">Title</Label>
            <Input id="title" name="title" required maxLength={300} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="content">Content (markdown ok)</Label>
            <TextareaWithMic id="content" name="content" rows={8} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="tags">Tags (comma-separated)</Label>
            <Input id="tags" name="tags" placeholder="grammar, vietnamese" />
          </div>
          <Button type="submit" className="w-full">
            Save
          </Button>
        </form>
      </aside>
    </div>
  );
}
