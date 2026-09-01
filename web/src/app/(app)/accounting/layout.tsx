import Link from "next/link";
import { requireViewer } from "@/lib/authorization";

const NAV_ITEMS = [
  { href: "/accounting", label: "Tổng quan" },
  { href: "/accounting/tax-calc", label: "Tính thuế" },
  { href: "/accounting/deadline", label: "Deadline" },
  { href: "/accounting/journal", label: "Định khoản" },
  { href: "/accounting/invoice", label: "Hoá đơn" },
  { href: "/accounting/reports", label: "Báo cáo" },
];

export default async function AccountingLayout({ children }: { children: React.ReactNode }) {
  // Kế toán viên (role=user) là daily-driver persona cho accounting tools.
  // Include 'user' để không block workflow (theo b28504f RBAC design).
  await requireViewer(["super_admin", "tenant_owner", "admin", "user"]);
  return (
    <div className="space-y-6">
      <nav className="-mx-1 flex flex-wrap gap-1 overflow-x-auto pb-2 text-sm">
        {NAV_ITEMS.map((item) => (
          <Link key={item.href} href={item.href} className="rounded-full border border-[var(--color-border)] px-3 py-1 text-[var(--color-muted-fg)] transition hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]">
            {item.label}
          </Link>
        ))}
      </nav>
      {children}
    </div>
  );
}
