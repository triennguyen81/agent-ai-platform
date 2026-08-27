import Link from "next/link";
import { Bot, Building2, ShieldCheck, Users } from "lucide-react";
import { requireViewer } from "@/lib/authorization";

export default async function DashboardPage() {
  const viewer = await requireViewer(["super_admin", "admin"]);
  const superAdmin = viewer.roles.includes("super_admin");
  const cards = superAdmin
    ? [
        ["Tenants", "/admin/tenants", Building2],
        ["Agent Builder", "/admin/agent-builder", Bot],
        ["Admin / Users", "/admin/users", Users],
        ["Audit", "/admin/audit", ShieldCheck],
      ] as const
    : [
        ["Người dùng tenant", "/admin/tenant-users", Users],
        ["Tenant Agents", "/admin/tenant-agents", Bot],
        ["Kiến thức", "/knowledge", Building2],
        ["Duyệt HITL", "/admin/hitl-queue", ShieldCheck],
      ] as const;
  return (
    <div className="space-y-6">
      <header><h1 className="font-serif text-3xl">Tổng quan</h1><p className="text-sm text-[var(--color-muted-fg)]">{superAdmin ? "Quản trị toàn hệ thống" : "Quản trị tenant đã xác thực"}</p></header>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {cards.map(([label, href, Icon]) => <Link key={href} href={href} className="rounded-[var(--radius)] border p-5 hover:border-[var(--color-primary)]"><Icon className="mb-3 size-5"/><span className="font-medium">{label}</span></Link>)}
      </div>
    </div>
  );
}
