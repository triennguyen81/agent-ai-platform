import Link from "next/link";
import { Bot, Building2, Landmark, ListChecks, ShieldCheck, Users, Workflow } from "lucide-react";
import { requireViewer } from "@/lib/authorization";

export default async function DashboardPage() {
  const viewer = await requireViewer(["super_admin", "tenant_owner", "admin"]);
  const superAdmin = viewer.roles.includes("super_admin");
  const tenantOwner = viewer.roles.includes("tenant_owner");
  const cards = superAdmin
    ? [
        ["Tenants", "/admin/tenants", Building2],
        ["Agent Builder", "/admin/agent-builder", Bot],
        ["Tenant Owners", "/system/tenant-owners", Users],
        ["Platform Audit", "/system/audit", ShieldCheck],
      ] as const
    : tenantOwner
      ? [
          ["Departments", "/admin/tenant-departments", Building2],
          ["Department Admins", "/tenant/department-admins", Users],
          ["Users", "/tenant/users", Users],
          ["Hồ sơ doanh nghiệp", "/tenant/business-profile", Landmark],
          ["Tenant Audit", "/tenant/audit", ShieldCheck],
        ] as const
      : [
          ["Department Agents", "/department/agents", Bot],
          ["Nghiệp vụ", "/department/capabilities", ListChecks],
          ["Workflows", "/department/workflows", Workflow],
          ["Review / HITL", "/department/review", ShieldCheck],
        ] as const;
  return (
    <div className="space-y-6">
      <header><h1 className="font-serif text-3xl">Tổng quan</h1><p className="text-sm text-[var(--color-muted-fg)]">{superAdmin ? "Platform / System" : tenantOwner ? "Company governance" : "Department AI Workspace"}</p></header>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {cards.map(([label, href, Icon]) => <Link key={href} href={href} className="rounded-[var(--radius)] border p-5 hover:border-[var(--color-primary)]"><Icon className="mb-3 size-5"/><span className="font-medium">{label}</span></Link>)}
      </div>
    </div>
  );
}
