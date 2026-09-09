import { requireViewer } from "@/lib/authorization";

// ACL V2-E: ADMIN's own Department Workflow ownership — department_admin
// only. SUPER_ADMIN's platform workflow catalog stays on /admin/workflows,
// unchanged; TENANT_OWNER has no operational workflow surface; USER never
// sees any workflow-management UI.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
