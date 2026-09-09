import { requireViewer } from "@/lib/authorization";

// ACL V2-E: ADMIN's own Department Skill ownership — department_admin only.
// SUPER_ADMIN's read-only filesystem skill viewer stays on /admin/skills,
// unchanged; TENANT_OWNER/USER never see any skill-management UI.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
