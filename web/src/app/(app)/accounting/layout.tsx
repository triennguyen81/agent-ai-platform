import Link from "next/link";
import { redirect } from "next/navigation";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";

const NAV_ITEMS: { href: string; label: string }[] = [
  { href: "/accounting", label: "Tổng quan" },
  { href: "/accounting/tax-calc", label: "Tính thuế" },
  { href: "/accounting/deadline", label: "Deadline" },
  { href: "/accounting/journal", label: "Định khoản" },
  { href: "/accounting/invoice", label: "Hoá đơn" },
  { href: "/accounting/reports", label: "Báo cáo" },
];

export default async function AccountingLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    const roles = me.roles ?? [];
    if (!roles.includes("super_admin") && !roles.includes("admin")) {
      redirect("/chat");
    }
  }

  return (
    <div className="space-y-6">
      <nav className="-mx-1 flex flex-wrap gap-1 overflow-x-auto pb-2 text-sm">
        {NAV_ITEMS.map((item) => (
          <Link
            key={item.href}
            href={item.href}
            className="rounded-full border border-[var(--color-border)] px-3 py-1 text-[var(--color-muted-fg)] transition hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
          >
            {item.label}
          </Link>
        ))}
      </nav>
      {children}
    </div>
  );
}
