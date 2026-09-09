import { requireViewer } from "@/lib/authorization";

// ACL V2-E: ADMIN's own Department Knowledge ownership — department_admin
// only. This is distinct from the existing shared /knowledge page (personal,
// per-user notes, unchanged); TENANT_OWNER has no operational Knowledge
// surface by default (Architecture Decisions §18's explicit non-decision);
// USER never sees any Knowledge-management UI.
export default async function Layout({ children }: { children: React.ReactNode }) {
  await requireViewer(["admin"]);
  return children;
}
