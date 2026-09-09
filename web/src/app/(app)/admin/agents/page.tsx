import { redirect } from "next/navigation";
import Link from "next/link";
import { requireViewer } from "@/lib/authorization";

// Compatibility bookmark only: each role lands on its single canonical UI.
export default async function Page() {
  const viewer = await requireViewer(["super_admin", "tenant_owner", "admin"]);
  if (!viewer.roles.includes("super_admin")) redirect("/admin/tenant-agents");
  return <section className="space-y-4"><h1 className="font-serif text-3xl">Platform Agents</h1><p className="text-sm text-[var(--color-muted-fg)]">This compatibility landing page contains no Department operational controls.</p><Link className="inline-flex rounded-[var(--radius)] border px-4 py-2 text-sm hover:border-[var(--color-primary)]" href="/admin/agent-builder">Open Platform Templates</Link></section>;
}
