import { redirect } from "next/navigation";
import Link from "next/link";
import { requireViewer } from "@/lib/authorization";

// Compatibility bookmark only: each role lands on its single canonical UI.
// ACL V2 final rule: TENANT_OWNER does not manage agents at all, so it no
// longer has an agent-related landing page here — /dashboard is their real
// home. ADMIN's canonical Agent surface is their own Department Agents.
export default async function Page() {
  const viewer = await requireViewer(["super_admin", "tenant_owner", "admin"]);
  if (viewer.roles.includes("super_admin")) {
    // falls through to the platform-templates landing below
  } else if (viewer.roles.includes("admin")) {
    redirect("/department/agents");
  } else {
    redirect("/dashboard");
  }
  return <section className="space-y-4"><h1 className="font-serif text-3xl">Platform Agents</h1><p className="text-sm text-[var(--color-muted-fg)]">This compatibility landing page contains no Department operational controls.</p><Link className="inline-flex rounded-[var(--radius)] border px-4 py-2 text-sm hover:border-[var(--color-primary)]" href="/admin/agent-builder">Open Platform Templates</Link></section>;
}
