import { requireViewer } from "@/lib/authorization";

// ACL V2-D: ADMIN's own department capability ("Nghiệp vụ") ownership —
// department_admin only. TENANT_OWNER keeps read-only oversight on the
// existing /admin/tenant-departments/[id] "Nghiệp vụ" tab (binding mutation
// removed from that page this phase); SUPER_ADMIN's platform catalog stays
// on /system/capabilities (no dedicated Web page — browsed today via the
// Agent Builder capability picker, out of scope to build further here);
// USER never sees any capability-management UI.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
