"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  BookOpen,
  Bot,
  Building2,
  ClipboardCheck,
  FileText,
  Gauge,
  GraduationCap,
  HeartPulse,
  ListChecks,
  LogOut,
  Puzzle,
  Receipt,
  ScrollText,
  ShieldCheck,
  UserCog,
  Users,
  Wrench,
  Workflow,
} from "lucide-react";
import { cn } from "@/lib/cn";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  NavTrigger,
} from "@/components/ui/dropdown-menu";

interface NavItem {
  href: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
}

function DirectLink({ href, label, active }: { href: string; label: string; active: boolean }) {
  return (
    <Link
      href={href}
      className={cn(
        "rounded-[var(--radius)] px-3 py-1.5 text-sm transition",
        active
          ? "bg-[var(--color-muted)] font-medium text-[var(--color-fg)]"
          : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]",
      )}
    >
      {label}
    </Link>
  );
}

function ModuleMenu({ label, items, pathname }: { label: string; items: NavItem[]; pathname: string }) {
  if (items.length === 0) return null;
  const isActive = items.some((i) => pathname.startsWith(i.href));
  return (
    <DropdownMenu>
      <NavTrigger className={isActive ? "bg-[var(--color-muted)] font-medium" : undefined}>{label}</NavTrigger>
      <DropdownMenuContent>
        {items.map((item) => (
          <DropdownMenuItem key={item.href} asChild>
            <Link href={item.href} className="flex items-center gap-2">
              <item.icon className="size-4 text-[var(--color-muted-fg)]" />
              {item.label}
            </Link>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function AccountTrigger({ email }: { email: string }) {
  const initial = email.trim().charAt(0).toUpperCase() || "?";
  return (
    <DropdownMenuTrigger className="flex items-center gap-2 rounded-[var(--radius)] px-2 py-1.5 text-sm outline-none transition hover:bg-[var(--color-muted)] data-[state=open]:bg-[var(--color-muted)]">
      <span className="flex size-7 items-center justify-center rounded-full bg-[var(--color-primary)] text-xs font-medium text-[var(--color-primary-fg)]">
        {initial}
      </span>
    </DropdownMenuTrigger>
  );
}

export interface AppNavProps {
  email: string;
  isSuperAdmin: boolean;
  isTenantAdmin: boolean;
  signOutAction: () => void | Promise<void>;
}

export function AppNav({ email, isSuperAdmin, isTenantAdmin, signOutAction }: AppNavProps) {
  const pathname = usePathname();
  const isAdminOrAbove = isSuperAdmin || isTenantAdmin;

  const nghiepVu: NavItem[] = isAdminOrAbove
    ? [
        { href: "/accounting", label: "Kế toán", icon: Receipt },
        { href: "/vouchers", label: "Chứng từ", icon: FileText },
        { href: "/contracts", label: "Hợp đồng", icon: ScrollText },
      ]
    : [];

  const workspace: NavItem[] = isAdminOrAbove
    ? [
        { href: "/knowledge", label: "Kiến thức", icon: BookOpen },
        { href: "/admin/agents", label: "Trợ lý AI", icon: Bot },
        ...(isSuperAdmin ? [{ href: "/admin/workflows", label: "Workflows", icon: Workflow }] : []),
        { href: "/admin/review-queue", label: "Reviews", icon: ListChecks },
        { href: "/admin/hitl-queue", label: "HITL", icon: ClipboardCheck },
      ]
    : [];

  const quanTri: NavItem[] = isSuperAdmin
    ? [
        { href: "/admin/agent-builder", label: "Agent Builder", icon: Puzzle },
        { href: "/admin/tenants", label: "Tenants", icon: Building2 },
        { href: "/admin/users", label: "Users", icon: UserCog },
        { href: "/admin/tools", label: "Tools", icon: Wrench },
        { href: "/admin/skills", label: "Skills", icon: GraduationCap },
        { href: "/admin/audit", label: "Audit", icon: ShieldCheck },
        { href: "/admin/health", label: "Health", icon: HeartPulse },
      ]
    : isTenantAdmin
      ? [
          { href: "/admin/tenant-users", label: "Users", icon: Users },
          { href: "/admin/tenant-agents", label: "Tenant Agents", icon: Bot },
          { href: "/knowledge", label: "Knowledge", icon: BookOpen },
          { href: "/admin/hitl-queue", label: "HITL", icon: ClipboardCheck },
          { href: "/admin/audit", label: "Audit / Usage", icon: ShieldCheck },
          { href: "/admin/tenant-status", label: "Tenant Status", icon: Gauge },
        ]
      : [];

  return (
    <nav className="flex items-center gap-1">
      <DirectLink href="/chat" label="Chat" active={pathname === "/chat"} />
      <DirectLink href="/my-agents" label="Trợ lý" active={pathname.startsWith("/my-agents")} />
      <ModuleMenu label="Nghiệp vụ" items={nghiepVu} pathname={pathname} />
      <ModuleMenu label="Workspace" items={workspace} pathname={pathname} />
      <ModuleMenu label="Quản trị" items={quanTri} pathname={pathname} />

      <div className="mx-1 h-5 w-px bg-[var(--color-border)]" />

      <DropdownMenu>
        <AccountTrigger email={email} />
        <DropdownMenuContent align="end">
          <div className="px-2.5 py-1.5 text-xs text-[var(--color-muted-fg)]">{email}</div>
          <DropdownMenuSeparator />
          <DropdownMenuItem asChild className="text-red-700">
            <form action={signOutAction}>
              <button type="submit" className="flex w-full items-center gap-2">
                <LogOut className="size-4" />
                Đăng xuất
              </button>
            </form>
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </nav>
  );
}
