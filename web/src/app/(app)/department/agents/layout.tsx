import { requireViewer } from "@/lib/authorization";

// ACL V2-C: ADMIN's own operational Agent Builder — department_admin only.
// TENANT_OWNER gets read-only oversight elsewhere (existing /admin/tenant-agents,
// unchanged); SUPER_ADMIN's Agent Builder is the separate platform-template
// surface at /admin/agent-builder; USER never sees an Agent Builder at all.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
