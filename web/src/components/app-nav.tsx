"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  Activity,
  BookOpen,
  Bot,
  Building2,
  Calculator,
  ChevronDown,
  ClipboardCheck,
  FileSignature,
  FileText,
  Heart,
  LayoutDashboard,
  MessageSquare,
  Receipt,
  Settings,
  Shield,
  ShieldAlert,
  Sparkles,
  Users,
  UsersRound,
  Wrench,
} from "lucide-react";

interface NavItem {
  href: string;
  label: string;
  icon: React.ElementType;
}

interface NavGroup {
  label: string;
  icon: React.ElementType;
  items: NavItem[];
}

interface AppNavProps {
  roles: string[];
}

export function AppNav({ roles }: AppNavProps) {
  const pathname = usePathname();
  const isSuperAdmin = roles.includes("super_admin");
  const canEditKnowledge = isSuperAdmin || roles.includes("admin");

  // Primary — always visible.
  const primary: NavItem[] = [
    { href: "/chat", label: "Chat", icon: MessageSquare },
    { href: "/my-agents", label: "Trợ lý", icon: Bot },
  ];

  // Nghiệp vụ — visible to admins (KB contributors).
  const business: NavGroup | null = canEditKnowledge
    ? {
        label: "Nghiệp vụ",
        icon: Calculator,
        items: [
          { href: "/accounting", label: "Kế toán", icon: Calculator },
          { href: "/vouchers", label: "Chứng từ", icon: Receipt },
          { href: "/customers", label: "Khách hàng", icon: Building2 },
          { href: "/contracts", label: "Hợp đồng", icon: FileSignature },
        ],
      }
    : null;

  // Kiến thức + Agent config.
  const workspace: NavGroup | null = canEditKnowledge
    ? {
        label: "Workspace",
        icon: BookOpen,
        items: [
          { href: "/knowledge", label: "Kiến thức", icon: BookOpen },
          { href: "/admin/agents", label: "Agents", icon: Bot },
          { href: "/admin/review-queue", label: "Reviews", icon: ClipboardCheck },
          { href: "/admin/hitl-queue", label: "HITL", icon: ShieldAlert },
        ],
      }
    : null;

  // Quản trị hệ thống — super_admin only.
  const admin: NavGroup | null = isSuperAdmin
    ? {
        label: "Quản trị",
        icon: Settings,
        items: [
          { href: "/admin/tenants", label: "Tenants", icon: Building2 },
          { href: "/admin/users", label: "Người dùng", icon: Users },
          { href: "/admin/tools", label: "Tools", icon: Wrench },
          { href: "/admin/skills", label: "Skills", icon: Sparkles },
          { href: "/admin/audit", label: "Audit", icon: Shield },
          { href: "/admin/health", label: "Health", icon: Heart },
        ],
      }
    : null;

  const isActive = (href: string) => {
    if (href === "/chat") return pathname === "/chat" || pathname.startsWith("/chat/");
    return pathname === href || pathname.startsWith(href + "/");
  };

  const groupActive = (group: NavGroup | null) =>
    group?.items.some((i) => isActive(i.href)) ?? false;

  return (
    <nav className="flex items-center gap-1 text-sm">
      {primary.map((item) => (
        <NavLink key={item.href} item={item} active={isActive(item.href)} />
      ))}
      {business && <NavDropdown group={business} isActive={isActive} active={groupActive(business)} />}
      {workspace && <NavDropdown group={workspace} isActive={isActive} active={groupActive(workspace)} />}
      {admin && <NavDropdown group={admin} isActive={isActive} active={groupActive(admin)} />}
    </nav>
  );
}

function NavLink({ item, active }: { item: NavItem; active: boolean }) {
  const Icon = item.icon;
  return (
    <Link
      href={item.href}
      className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 transition ${
        active
          ? "bg-[var(--color-primary)] text-white shadow-sm"
          : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]"
      }`}
    >
      <Icon className="size-3.5" />
      <span>{item.label}</span>
    </Link>
  );
}

function NavDropdown({
  group,
  isActive,
  active,
}: {
  group: NavGroup;
  isActive: (href: string) => boolean;
  active: boolean;
}) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const Icon = group.icon;

  // Small delay before closing so the cursor can cross the gap between
  // the trigger button and the dropdown panel without the menu vanishing.
  const scheduleClose = () => {
    if (closeTimer.current) clearTimeout(closeTimer.current);
    closeTimer.current = setTimeout(() => setOpen(false), 200);
  };
  const cancelClose = () => {
    if (closeTimer.current) {
      clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  };

  // Click-outside closes the menu (needed for the tap-to-open path on
  // touch devices where mouseleave never fires).
  useEffect(() => {
    if (!open) return;
    const onDocClick = (e: MouseEvent) => {
      if (!containerRef.current?.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", onDocClick);
    return () => document.removeEventListener("mousedown", onDocClick);
  }, [open]);

  useEffect(() => () => cancelClose(), []);

  return (
    <div
      ref={containerRef}
      className="relative"
      onMouseEnter={() => {
        cancelClose();
        setOpen(true);
      }}
      onMouseLeave={scheduleClose}
    >
      <button
        type="button"
        className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 transition ${
          active
            ? "bg-[var(--color-primary)]/15 text-[var(--color-primary)] font-medium"
            : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]"
        }`}
        onClick={() => setOpen((prev) => !prev)}
        aria-expanded={open}
      >
        <Icon className="size-3.5" />
        <span>{group.label}</span>
        <ChevronDown className={`size-3 transition ${open ? "rotate-180" : ""}`} />
      </button>
      {open && (
        // top-full puts the panel flush against the button (no gap for the
        // cursor to fall through). pt-1 inside the panel gives visual room.
        <div className="absolute right-0 top-full z-30 w-56 pt-1">
          <div className="rounded-lg border bg-[var(--color-bg)] p-1 shadow-lg">
            {group.items.map((item) => {
              const ItemIcon = item.icon;
              const isItemActive = isActive(item.href);
              return (
                <Link
                  key={item.href}
                  href={item.href}
                  onClick={() => setOpen(false)}
                  className={`flex items-center gap-2 rounded-md px-3 py-2 text-sm transition ${
                    isItemActive
                      ? "bg-[var(--color-primary)] text-white"
                      : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]"
                  }`}
                >
                  <ItemIcon className="size-4" />
                  <span>{item.label}</span>
                </Link>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
