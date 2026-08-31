import { requireViewer } from "@/lib/authorization";

export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  await requireViewer(["super_admin", "tenant_owner", "admin"]);
  return children;
}
