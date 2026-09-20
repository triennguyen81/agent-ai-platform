import { requireViewer } from "@/lib/authorization";

// ACL V2-C: ADMIN's own operational Agent Builder — department_admin only.
// ACL V2 final rule: TENANT_OWNER does not manage agents at all, not even
// read-only oversight — it has no view of this surface. SUPER_ADMIN's
// Agent Builder is the separate platform-template surface at
// /admin/agent-builder; USER never sees an Agent Builder at all.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
