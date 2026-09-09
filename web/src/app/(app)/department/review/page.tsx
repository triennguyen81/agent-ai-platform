import Link from "next/link";
export default function Page() {
  return <div className="space-y-6"><header><h1 className="font-serif text-3xl">Review / HITL</h1><p className="mt-2 text-sm text-[var(--color-muted-fg)]">Own-department review and human approval operations.</p></header><div className="grid gap-4 sm:grid-cols-2"><Link className="rounded-[var(--radius)] border p-5 hover:border-[var(--color-primary)]" href="/admin/review-queue">Review Queue</Link><Link className="rounded-[var(--radius)] border p-5 hover:border-[var(--color-primary)]" href="/admin/hitl-queue">HITL Queue</Link></div></div>;
}
