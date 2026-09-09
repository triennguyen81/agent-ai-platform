import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireViewer } from "@/lib/authorization";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ResourceScopeBadge } from "@/components/resource-scope-badge";

async function createAction(data: FormData) { "use server"; const v=await requireViewer(["super_admin"]); const r=await gateway.createPlatformCapability(v.token,{code:String(data.get("code")??"").trim(),name:String(data.get("name")??"").trim()}); if(!r.ok) redirect("/system/capabilities?error=create"); revalidatePath("/system/capabilities"); }
async function toggleAction(data: FormData) { "use server"; const v=await requireViewer(["super_admin"]); await gateway.setPlatformCapabilityEnabled(v.token,String(data.get("id")),String(data.get("enabled"))==="true"); revalidatePath("/system/capabilities"); }

export default async function PlatformCapabilitiesPage() {
  const v=await requireViewer(["super_admin"]); const r=await gateway.listCapabilityCatalog(v.token);
  const items=r.ok?((await r.json()) as {items:{id:string;code:string;name:string;isActive:boolean}[]}).items:[];
  return <div className="space-y-6"><header><h1 className="font-serif text-3xl">Nghiệp vụ — Hệ thống</h1><p className="text-sm text-[var(--color-muted-fg)]">Danh mục Nghiệp vụ Hệ thống dùng chung cho mọi Department Workspace.</p></header>
    <form action={createAction} className="flex gap-2 rounded border p-4"><Input name="code" required placeholder="domain.action"/><Input name="name" required placeholder="Tên nghiệp vụ"/><Button>Tạo</Button></form>
    <div className="space-y-2">{items.map(i=><div key={i.id} className="flex items-center justify-between rounded border p-3"><div><b>{i.name}</b><p className="font-mono text-xs">{i.code}</p><ResourceScopeBadge scope="platform"/></div><form action={toggleAction}><input type="hidden" name="id" value={i.id}/><input type="hidden" name="enabled" value={String(!i.isActive)}/><Button variant="outline" size="sm">{i.isActive?"Disable":"Enable"}</Button></form></div>)}</div></div>;
}
