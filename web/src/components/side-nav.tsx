"use client";

import { useState } from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import {
  Banknote,
  Bot,
  BookOpen,
  Building2,
  Calculator,
  Check,
  ClipboardCheck,
  FileText,
  Gauge,
  GraduationCap,
  HeartPulse,
  Home,
  ListChecks,
  LogOut,
  MessageSquare,
  Plus,
  Puzzle,
  Receipt,
  ScrollText,
  Settings,
  ShieldCheck,
  Sparkles,
  Truck,
  UserCog,
  Users,
  Wrench,
  Workflow,
} from "lucide-react";
import { cn } from "@/lib/cn";

// SideNav — Claude.ai-style sidebar. Layout: brand + New chat + primary
// nav + module sections + agent list (như "Projects" bên Claude).
// User chọn agent trong list → set active_agent cookie qua /api/me/active-agent.
// Tất cả tính năng dùng /agent/run sẽ tự động dùng agent này.

export interface SideNavAgent {
  agentId: string;
  agentName: string;
  agentCode: string;
  lifecycleState: string;
  agentDisabled: boolean;
}

interface SideNavProps {
  email: string;
  isSuperAdmin: boolean;
  isTenantOwner: boolean;
  isTenantAdmin: boolean;
  agents: SideNavAgent[];
  activeAgentId: string | null;
  signOutAction: () => void | Promise<void>;
}

export function SideNav({
  email,
  isSuperAdmin,
  isTenantOwner,
  isTenantAdmin,
  agents,
  activeAgentId,
  signOutAction,
}: SideNavProps) {
  const pathname = usePathname();
  const router = useRouter();
  const isAdminOrAbove = isSuperAdmin || isTenantOwner || isTenantAdmin;
  const [switching, setSwitching] = useState<string | null>(null);

  // Usable agents (production + not-disabled) — người dùng chỉ switch được
  // sang những agent này. Others hiển thị grey disabled ở dưới.
  const usableAgents = agents.filter(
    (a) => a.lifecycleState === "production" && !a.agentDisabled,
  );
  const disabledAgents = agents.filter(
    (a) => a.lifecycleState !== "production" || a.agentDisabled,
  );

  const activeAgent = agents.find((a) => a.agentId === activeAgentId) ?? usableAgents[0];

  const switchAgent = async (agentId: string) => {
    if (switching || agentId === activeAgentId) return;
    setSwitching(agentId);
    try {
      const res = await fetch("/api/me/active-agent", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ agentId }),
      });
      if (res.ok) {
        router.refresh();
      }
    } finally {
      setSwitching(null);
    }
  };

  return (
    <aside className="flex h-screen w-64 shrink-0 flex-col border-r border-[var(--color-border)] bg-[var(--color-bg)]">
      {/* Brand */}
      <div className="border-b border-[var(--color-border)] p-4">
        <Link
          href={isAdminOrAbove ? "/dashboard" : "/my-agents"}
          className="font-serif text-xl tracking-tight text-[var(--color-fg)]"
        >
          Cát Tường AI
        </Link>
        {activeAgent && (
          <div className="mt-2 text-[11px] text-[var(--color-muted-fg)]">
            Đang dùng: <span className="font-medium text-[var(--color-fg)]">{activeAgent.agentName}</span>
          </div>
        )}
      </div>

      {/* Scrollable content */}
      <div className="flex-1 overflow-y-auto p-3">
        {/* Quick action */}
        <SidebarLink href="/chat" icon={Plus} label="New chat" active={pathname === "/chat"} />

        {/* Primary nav */}
        <div className="mt-3 space-y-0.5">
          {isAdminOrAbove && (
            <SidebarLink href="/dashboard" icon={Home} label="Dashboard" active={pathname === "/dashboard"} />
          )}
          <SidebarLink href="/my-agents" icon={Bot} label="Trợ lý AI" active={pathname.startsWith("/my-agents")} />
          <SidebarLink href="/chat" icon={MessageSquare} label="Chat" active={pathname.startsWith("/chat")} />
        </div>

        {/* Nghiệp vụ section */}
        <SidebarSection title="Nghiệp vụ">
          <SidebarLink href="/vouchers" icon={FileText} label="Chứng từ" active={pathname.startsWith("/vouchers")} />
          <SidebarLink href="/accounting" icon={Calculator} label="Kế toán" active={pathname.startsWith("/accounting")} />
          <SidebarLink href="/customers" icon={Building2} label="Khách hàng" active={pathname.startsWith("/customers")} />
          <SidebarLink href="/suppliers" icon={Truck} label="Nhà cung cấp" active={pathname.startsWith("/suppliers")} />
          <SidebarLink href="/expenses" icon={Banknote} label="Chi phí" active={pathname.startsWith("/expenses")} />
          <SidebarLink href="/mau-chung-tu" icon={GraduationCap} label="Chứng từ mẫu" active={pathname.startsWith("/mau-chung-tu")} />
          {isAdminOrAbove && (
            <SidebarLink href="/contracts" icon={ScrollText} label="Hợp đồng" active={pathname.startsWith("/contracts")} />
          )}
        </SidebarSection>

        {/* Workspace (admin+) */}
        {isAdminOrAbove && (
          <SidebarSection title="Workspace">
            <SidebarLink href="/knowledge" icon={BookOpen} label="Kiến thức" active={pathname.startsWith("/knowledge")} />
            <SidebarLink href="/admin/agents" icon={Bot} label="Agents" active={pathname === "/admin/agents"} />
            {isSuperAdmin && (
              <SidebarLink href="/admin/workflows" icon={Workflow} label="Workflows" active={pathname.startsWith("/admin/workflows")} />
            )}
            <SidebarLink href="/admin/review-queue" icon={ListChecks} label="Reviews" active={pathname.startsWith("/admin/review-queue")} />
            <SidebarLink href="/admin/hitl-queue" icon={ClipboardCheck} label="HITL" active={pathname.startsWith("/admin/hitl-queue")} />
          </SidebarSection>
        )}

        {/* Quản trị (super_admin / tenant_owner / tenant_admin) */}
        {isSuperAdmin && (
          <SidebarSection title="Quản trị">
            <SidebarLink href="/admin/agent-builder" icon={Puzzle} label="Agent Builder" active={pathname.startsWith("/admin/agent-builder")} />
            <SidebarLink href="/admin/tenants" icon={Building2} label="Tenants" active={pathname.startsWith("/admin/tenants")} />
            <SidebarLink href="/admin/users" icon={UserCog} label="Users" active={pathname === "/admin/users"} />
            <SidebarLink href="/admin/tools" icon={Wrench} label="Tools" active={pathname.startsWith("/admin/tools")} />
            <SidebarLink href="/admin/skills" icon={Sparkles} label="Skills" active={pathname.startsWith("/admin/skills")} />
            <SidebarLink href="/admin/audit" icon={ShieldCheck} label="Audit" active={pathname.startsWith("/admin/audit")} />
            <SidebarLink href="/admin/health" icon={HeartPulse} label="Health" active={pathname.startsWith("/admin/health")} />
          </SidebarSection>
        )}

        {isTenantOwner && !isSuperAdmin && (
          <SidebarSection title="Quản trị tenant">
            <SidebarLink href="/admin/tenant-admins" icon={UserCog} label="Administrators" active={pathname.startsWith("/admin/tenant-admins")} />
            <SidebarLink href="/admin/tenant-users" icon={Users} label="Users" active={pathname.startsWith("/admin/tenant-users")} />
            <SidebarLink href="/admin/tenant-agents" icon={Bot} label="Tenant Agents" active={pathname.startsWith("/admin/tenant-agents")} />
            <SidebarLink href="/knowledge" icon={BookOpen} label="Knowledge" active={pathname === "/knowledge"} />
            <SidebarLink href="/admin/hitl-queue" icon={ClipboardCheck} label="HITL" active={pathname.startsWith("/admin/hitl-queue")} />
            <SidebarLink href="/admin/audit" icon={ShieldCheck} label="Audit" active={pathname.startsWith("/admin/audit")} />
            <SidebarLink href="/admin/tenant-status" icon={Gauge} label="Tenant Status" active={pathname.startsWith("/admin/tenant-status")} />
          </SidebarSection>
        )}

        {isTenantAdmin && !isTenantOwner && !isSuperAdmin && (
          <SidebarSection title="Quản trị">
            <SidebarLink href="/admin/tenant-users" icon={Users} label="Users" active={pathname.startsWith("/admin/tenant-users")} />
            <SidebarLink href="/admin/tenant-agents" icon={Bot} label="Tenant Agents" active={pathname.startsWith("/admin/tenant-agents")} />
            <SidebarLink href="/knowledge" icon={BookOpen} label="Knowledge" active={pathname === "/knowledge"} />
            <SidebarLink href="/admin/hitl-queue" icon={ClipboardCheck} label="HITL" active={pathname.startsWith("/admin/hitl-queue")} />
            <SidebarLink href="/admin/audit" icon={ShieldCheck} label="Audit" active={pathname.startsWith("/admin/audit")} />
          </SidebarSection>
        )}

        {/* AGENT LIST — Claude.ai-style "Projects" section.
            Mỗi agent là 1 project/specialist context. Click = switch. */}
        {agents.length > 0 && (
          <SidebarSection title="Trợ lý của bạn">
            {usableAgents.map((a) => {
              const isActive = a.agentId === activeAgentId
                || (!activeAgentId && a === usableAgents[0]);
              const isSwitching = switching === a.agentId;
              return (
                <button
                  key={a.agentId}
                  type="button"
                  onClick={() => switchAgent(a.agentId)}
                  disabled={isSwitching}
                  title={a.agentCode}
                  className={cn(
                    "flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition",
                    isActive
                      ? "bg-[var(--color-primary)]/10 font-medium text-[var(--color-primary)]"
                      : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]",
                    isSwitching && "opacity-50",
                  )}
                >
                  <span className="flex size-4 shrink-0 items-center justify-center">
                    {isActive ? (
                      <span className="block size-2 rounded-full bg-[var(--color-primary)]" />
                    ) : (
                      <span className="block size-2 rounded-full border border-[var(--color-border)]" />
                    )}
                  </span>
                  <span className="truncate flex-1">{a.agentName}</span>
                  {isActive && <Check className="size-3 shrink-0 text-[var(--color-primary)]" />}
                </button>
              );
            })}

            {disabledAgents.length > 0 && (
              <details className="mt-2">
                <summary className="cursor-pointer px-2 py-1 text-[11px] text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]">
                  Chưa sẵn sàng ({disabledAgents.length})
                </summary>
                <div className="mt-1 space-y-0.5">
                  {disabledAgents.map((a) => (
                    <div
                      key={a.agentId}
                      className="flex items-center gap-2 px-2 py-1 text-xs text-[var(--color-muted-fg)] opacity-70"
                      title={a.agentDisabled ? "Admin đã khoá" : a.lifecycleState}
                    >
                      <span className="block size-2 shrink-0 rounded-full border border-[var(--color-border)]" />
                      <span className="truncate">{a.agentName}</span>
                    </div>
                  ))}
                </div>
              </details>
            )}
          </SidebarSection>
        )}
      </div>

      {/* Footer: user info + logout */}
      <div className="border-t border-[var(--color-border)] p-3">
        <div className="flex items-center gap-2">
          <span className="flex size-8 items-center justify-center rounded-full bg-[var(--color-primary)] text-sm font-medium text-white">
            {email.charAt(0).toUpperCase()}
          </span>
          <div className="min-w-0 flex-1">
            <div className="truncate text-xs font-medium">{email}</div>
            <div className="text-[10px] text-[var(--color-muted-fg)]">
              {isSuperAdmin ? "super_admin" : isTenantOwner ? "tenant_owner" : isTenantAdmin ? "admin" : "user"}
            </div>
          </div>
          <form action={signOutAction}>
            <button
              type="submit"
              title="Đăng xuất"
              className="rounded-md p-1.5 text-[var(--color-muted-fg)] transition hover:bg-red-50 hover:text-red-600"
            >
              <LogOut className="size-4" />
            </button>
          </form>
        </div>
      </div>
    </aside>
  );
}

function SidebarSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mt-4">
      <div className="mb-1 px-2 text-[10px] font-semibold uppercase tracking-wider text-[var(--color-muted-fg)]">
        {title}
      </div>
      <div className="space-y-0.5">{children}</div>
    </div>
  );
}

interface SidebarLinkProps {
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  active: boolean;
}

function SidebarLink({ href, icon: Icon, label, active }: SidebarLinkProps) {
  return (
    <Link
      href={href}
      className={cn(
        "flex items-center gap-2 rounded-md px-2 py-1.5 text-sm transition",
        active
          ? "bg-[var(--color-muted)] font-medium text-[var(--color-fg)]"
          : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]",
      )}
    >
      <Icon className="size-4 shrink-0 text-[var(--color-muted-fg)]" />
      <span className="truncate">{label}</span>
    </Link>
  );
}
