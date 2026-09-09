import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input, Textarea } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";

async function createAction(data:FormData){"use server";const v=await requireViewer(["super_admin"]);const r=await gateway.createPlatformKnowledge(v.token,{title:String(data.get("title")??"").trim(),content:String(data.get("content")??"")});if(!r.ok)redirect("/system/knowledge?error=create");revalidatePath("/system/knowledge");}
async function archiveAction(data:FormData){"use server";const v=await requireViewer(["super_admin"]);await gateway.archivePlatformKnowledge(v.token,String(data.get("id")));revalidatePath("/system/knowledge");}
async function deleteAction(data:FormData){"use server";const v=await requireViewer(["super_admin"]);await gateway.deletePlatformKnowledge(v.token,String(data.get("id")));revalidatePath("/system/knowledge");}
export default async function PlatformKnowledgePage(){const v=await requireViewer(["super_admin"]);const r=await gateway.listPlatformKnowledge(v.token);const items=r.ok?((await r.json())as{items:{id:string;title:string;content:string}[]}).items:[];return <div className="space-y-6"><header><h1 className="font-serif text-3xl">Global Knowledge</h1><p className="text-sm text-[var(--color-muted-fg)]">Knowledge Template dùng chung; ADMIN chỉ đọc và attach vào Agent của phòng ban.</p></header><form action={createAction} className="space-y-2 rounded border p-4"><Input name="title" required placeholder="Tiêu đề"/><Textarea name="content" rows={5} placeholder="Nội dung"/><Button>Tạo</Button></form><div className="space-y-2">{items.map(i=><div key={i.id} className="flex items-start justify-between rounded border p-3"><div><b>{i.title}</b><ResourceScopeBadge scope="platform"/><p className="mt-1 line-clamp-2 text-xs text-[var(--color-muted-fg)]">{i.content}</p></div><div className="flex gap-2"><form action={archiveAction}><input type="hidden" name="id" value={i.id}/><Button size="sm" variant="outline">Archive</Button></form><form action={deleteAction}><input type="hidden" name="id" value={i.id}/><Button size="sm" variant="ghost">Delete</Button></form></div></div>)}</div></div>}
