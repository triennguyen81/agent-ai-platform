import Link from "next/link";
import { requireViewer } from "@/lib/authorization";
import { fetchMyCapabilityCodes } from "@/lib/my-capabilities";

// D7 §7: sub-nav tabs backed by a business capability are shown only when
// the viewer is actually authorized for that capability, instead of a
// static role-only list — avoids advertising a dead-end tab. This is
// presentation configuration only: the capability code drives which link
// renders, never an authorization decision (the Gateway resolver — D4 — and
// each page's own executeCapability call remain the sole authority, and
// still fail closed regardless of what renders here).
const ALWAYS_ITEMS = [
  { href: "/accounting", label: "Tổng quan" },
  { href: "/accounting/deadline", label: "Deadline" },
];

const CAPABILITY_ITEMS: { href: string; label: string; capability: string }[] = [
  { href: "/accounting/tax-calc", label: "Tính thuế", capability: "accounting.tax" },
  { href: "/accounting/journal", label: "Định khoản", capability: "accounting.journal" },
  { href: "/accounting/invoice", label: "Hoá đơn", capability: "accounting.invoice" },
  { href: "/accounting/reports", label: "Báo cáo", capability: "accounting.report" },
];

export default async function AccountingLayout({ children }: { children: React.ReactNode }) {
  // Kế toán viên (role=user) là daily-driver persona cho accounting tools.
  // Include 'user' để không block workflow (theo b28504f RBAC design).
  const viewer = await requireViewer(["admin", "user"]);

  // `null` (owner/super_admin — not capability-gated) keeps the full,
  // unfiltered tab set, matching their existing (unchanged) access here.
  const codes = await fetchMyCapabilityCodes(viewer.token, viewer.roles);
  const visibleCapabilityItems = codes ? CAPABILITY_ITEMS.filter((item) => codes.has(item.capability)) : CAPABILITY_ITEMS;
  const navItems = [...ALWAYS_ITEMS, ...visibleCapabilityItems];

  return (
    <div className="space-y-6">
      <nav className="-mx-1 flex flex-wrap gap-1 overflow-x-auto pb-2 text-sm">
        {navItems.map((item) => (
          <Link key={item.href} href={item.href} className="rounded-full border border-[var(--color-border)] px-3 py-1 text-[var(--color-muted-fg)] transition hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]">
            {item.label}
          </Link>
        ))}
      </nav>
      {children}
    </div>
  );
}
