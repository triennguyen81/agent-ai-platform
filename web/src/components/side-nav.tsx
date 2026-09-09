"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { Bot, Check, ChevronRight, Home, LogOut, Menu, MessageSquare, Plus, Receipt, X } from "lucide-react";
import { cn } from "@/lib/cn";
import { buildNghiepVuItems, buildQuanTriGroups, buildWorkspaceItems, canUseChat, type RoleFlags } from "@/lib/nav-model";

// SideNav — Claude.ai-style sidebar. Layout: brand + New chat + primary
// nav + module sections + agent list ("Trợ lý của bạn" = "Projects") +
// recent chats. Phase 2: mobile drawer, agent avatar, recent chats.
//
// Business/Workspace/Quản trị sections are driven entirely by
// nav-model.ts's buildNghiepVuItems/buildWorkspaceItems/buildQuanTriGroups
// (the ACL V2-F exact navigation matrix — see nav-model.test.ts and
// docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md) so this component never
// hard-codes which pages a role can see; it only renders whatever that
// model returns. Access is still enforced server-side by each route's own
// layout guard (requireViewer/role checks) regardless of what's rendered
// here — this is presentation only, not a security boundary.

export interface SideNavAgent {
  agentId: string;
  agentName: string;
  agentCode: string;
  lifecycleState: string;
  agentDisabled: boolean;
}

interface RecentThread {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
}

interface SideNavProps {
  email: string;
  isSuperAdmin: boolean;
  isTenantOwner: boolean;
  isTenantAdmin: boolean;
  capabilityCodes: readonly string[];
  agents: SideNavAgent[];
  activeAgentId: string | null;
  signOutAction: () => void | Promise<void>;
}

// Auto-generate màu ổn định từ agentCode. Dùng cho avatar circle của agent.
// Hash simple → pick từ palette 8 màu (không dùng đỏ để tránh trùng error tone).
const AVATAR_COLORS = [
  { bg: "bg-emerald-500", fg: "text-white" },
  { bg: "bg-blue-500", fg: "text-white" },
  { bg: "bg-indigo-500", fg: "text-white" },
  { bg: "bg-purple-500", fg: "text-white" },
  { bg: "bg-pink-500", fg: "text-white" },
  { bg: "bg-amber-500", fg: "text-white" },
  { bg: "bg-cyan-500", fg: "text-white" },
  { bg: "bg-teal-500", fg: "text-white" },
];

function avatarForAgent(code: string): { bg: string; fg: string; initial: string } {
  let hash = 0;
  for (let i = 0; i < code.length; i++) {
    hash = (hash * 31 + code.charCodeAt(i)) | 0;
  }
  const color = AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length];
  // First 2 alphanumeric characters, uppercase
  const cleaned = code.replace(/[^a-zA-Z0-9]/g, "").toUpperCase();
  const initial = cleaned.slice(0, 2) || "??";
  return { ...color, initial };
}

function formatRelativeTime(ts: number): string {
  const now = Date.now();
  const diffMs = now - ts * 1000;
  const mins = Math.floor(diffMs / 60000);
  if (mins < 1) return "vừa xong";
  if (mins < 60) return `${mins}p`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h`;
  const days = Math.floor(hrs / 24);
  if (days < 7) return `${days}d`;
  return `${Math.floor(days / 7)}w`;
}

export function SideNav({
  email,
  isSuperAdmin,
  isTenantOwner,
  isTenantAdmin,
  capabilityCodes,
  agents,
  activeAgentId,
  signOutAction,
}: SideNavProps) {
  const pathname = usePathname();
  const router = useRouter();
  const isAdminOrAbove = isSuperAdmin || isTenantOwner || isTenantAdmin;

  const flags: RoleFlags = { isSuperAdmin, isTenantOwner, isTenantAdmin, capabilityCodes };
  const nghiepVuItems = buildNghiepVuItems(flags);
  const workspaceItems = buildWorkspaceItems(flags);
  const quanTriGroups = buildQuanTriGroups(flags);
  // SUPER_ADMIN/TENANT_OWNER administer tenants/departments, not day-to-day
  // AI Workspace usage — no Chat, New chat, agent switcher, or recent chats.
  const chatEnabled = canUseChat(flags);

  const [switching, setSwitching] = useState<string | null>(null);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [recentThreads, setRecentThreads] = useState<RecentThread[]>([]);

  // Close mobile drawer khi route thay đổi.
  useEffect(() => {
    setMobileOpen(false);
  }, [pathname]);

  // Fetch recent chat threads (client-side, không blocking layout).
  // Skipped entirely for roles without Chat (SUPER_ADMIN/TENANT_OWNER).
  useEffect(() => {
    if (!chatEnabled) return;
    let cancelled = false;
    fetch("/api/chat/threads")
      .then((r) => (r.ok ? r.json() : { items: [] }))
      .then((body: { items?: RecentThread[] }) => {
        if (!cancelled) {
          const items = body.items ?? [];
          // Sort by updated_at desc, take top 5
          items.sort((a, b) => b.updated_at - a.updated_at);
          setRecentThreads(items.slice(0, 5));
        }
      })
      .catch(() => {
        /* silent — recent chats không critical */
      });
    return () => {
      cancelled = true;
    };
  }, [chatEnabled]);

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
    <>
      {/* Mobile hamburger — hidden on md+ */}
      <button
        type="button"
        onClick={() => setMobileOpen(true)}
        className="fixed left-3 top-3 z-30 flex size-9 items-center justify-center rounded-md border border-[var(--color-border)] bg-[var(--color-bg)] shadow-sm md:hidden"
        aria-label="Mở menu"
      >
        <Menu className="size-5" />
      </button>

      {/* Backdrop overlay (mobile only) */}
      {mobileOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/40 backdrop-blur-sm md:hidden"
          onClick={() => setMobileOpen(false)}
          aria-hidden="true"
        />
      )}

      {/* Sidebar */}
      <aside
        className={cn(
          "flex h-screen w-64 shrink-0 flex-col border-r border-[var(--color-border)] bg-[var(--color-bg)]",
          // Desktop: static; Mobile: fixed slide-in
          "fixed inset-y-0 left-0 z-40 transition-transform md:static md:translate-x-0",
          mobileOpen ? "translate-x-0" : "-translate-x-full",
        )}
      >
        {/* Brand + close btn (mobile) */}
        <div className="flex items-start justify-between border-b border-[var(--color-border)] p-4">
          <div className="min-w-0 flex-1">
            <Link
              href={isAdminOrAbove ? "/dashboard" : "/my-agents"}
              className="font-serif text-xl tracking-tight text-[var(--color-fg)]"
            >
              Cát Tường AI
            </Link>
            {chatEnabled && activeAgent && (
              <div className="mt-2 flex items-center gap-2 text-[11px] text-[var(--color-muted-fg)]">
                <span>Đang dùng:</span>
                <AgentAvatar code={activeAgent.agentCode} size="sm" />
                <span className="truncate font-medium text-[var(--color-fg)]">
                  {activeAgent.agentName}
                </span>
              </div>
            )}
          </div>
          <button
            type="button"
            onClick={() => setMobileOpen(false)}
            className="ml-2 rounded p-1 text-[var(--color-muted-fg)] hover:bg-[var(--color-muted)] md:hidden"
            aria-label="Đóng menu"
          >
            <X className="size-4" />
          </button>
        </div>

        {/* Scrollable content */}
        <div className="flex-1 overflow-y-auto p-3">
          {/* Quick action — no Chat at all for SUPER_ADMIN/TENANT_OWNER */}
          {chatEnabled && (
            <SidebarLink href="/chat" icon={Plus} label="New chat" active={pathname === "/chat"} />
          )}

          {/* Primary nav */}
          <div className={cn(chatEnabled && "mt-3", "space-y-0.5")}>
            {isAdminOrAbove && (
              <SidebarLink href="/dashboard" icon={Home} label="Dashboard" active={pathname === "/dashboard"} />
            )}
            {chatEnabled && (
              <>
                <SidebarLink href="/my-agents" icon={Bot} label="Trợ lý AI" active={pathname.startsWith("/my-agents")} />
                <SidebarLink href="/chat" icon={MessageSquare} label="Chat" active={pathname.startsWith("/chat")} />
              </>
            )}
          </div>

          {/* AGENT LIST — Claude.ai-style "Projects" section.
              Position on top so switching is prominent. */}
          {chatEnabled && agents.length > 0 && (
            <SidebarSection title="Trợ lý của bạn" collapsible={false}>
              {usableAgents.map((a) => {
                const isActive = a.agentId === activeAgentId
                  || (!activeAgentId && a === usableAgents[0]);
                const isSwitching = switching === a.agentId;
                const avatar = avatarForAgent(a.agentCode);
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
                    <AgentAvatarByColors bg={avatar.bg} fg={avatar.fg} initial={avatar.initial} />
                    <span className="flex-1 truncate">{a.agentName}</span>
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
                    {disabledAgents.map((a) => {
                      const avatar = avatarForAgent(a.agentCode);
                      return (
                        <div
                          key={a.agentId}
                          className="flex items-center gap-2 px-2 py-1 text-xs text-[var(--color-muted-fg)] opacity-60"
                          title={a.agentDisabled ? "Admin đã khoá" : a.lifecycleState}
                        >
                          <AgentAvatarByColors bg={avatar.bg} fg={avatar.fg} initial={avatar.initial} />
                          <span className="truncate">{a.agentName}</span>
                        </div>
                      );
                    })}
                  </div>
                </details>
              )}
            </SidebarSection>
          )}

          {/* Recent chats — top 5 threads gần nhất */}
          {chatEnabled && recentThreads.length > 0 && (
            <SidebarSection title="Chat gần đây" collapsible={false}>
              {recentThreads.map((t) => (
                <Link
                  key={t.thread_id}
                  href={`/chat?thread=${encodeURIComponent(t.thread_id)}`}
                  className="flex items-center gap-2 rounded-md px-2 py-1 text-xs text-[var(--color-fg)] transition hover:bg-[var(--color-muted)]"
                  title={t.title}
                >
                  <MessageSquare className="size-3 shrink-0 text-[var(--color-muted-fg)]" />
                  <span className="flex-1 truncate">{t.title}</span>
                  <span className="shrink-0 text-[10px] text-[var(--color-muted-fg)]">
                    {formatRelativeTime(t.updated_at)}
                  </span>
                </Link>
              ))}
            </SidebarSection>
          )}

          {/* Nghiệp vụ — capability-driven business pages (nav-model.ts
              buildNghiepVuItems). Empty for SUPER_ADMIN/TENANT_OWNER; for
              ADMIN/USER, only pages backed by an actually-assigned
              capability ever appear. */}
          {nghiepVuItems.length > 0 && (
            <SidebarSection
              title="Nghiệp vụ"
              active={nghiepVuItems.some((item) => pathname.startsWith(item.href))}
            >
              {nghiepVuItems.map((item) => (
                <SidebarLink
                  key={item.href}
                  href={item.href}
                  icon={item.icon}
                  label={item.label}
                  active={pathname.startsWith(item.href)}
                />
              ))}
            </SidebarSection>
          )}

          {/* AI Workspace — ADMIN's unified own-department resource list
              (nav-model.ts buildWorkspaceItems): Agents, Nghiệp vụ,
              Workflows, Skills, Knowledge, Tools. Empty for every other
              role — SUPER_ADMIN/TENANT_OWNER never get department
              operational resource links here. */}
          {workspaceItems.length > 0 && (
            <SidebarSection
              title="Workspace"
              active={workspaceItems.some((item) => pathname.startsWith(item.href))}
            >
              {workspaceItems.map((item) => (
                <SidebarLink
                  key={item.href}
                  href={item.href}
                  icon={item.icon}
                  label={item.label}
                  active={pathname.startsWith(item.href)}
                />
              ))}
            </SidebarSection>
          )}

          {/* Quản trị — role-scoped governance groups (nav-model.ts
              buildQuanTriGroups): "Platform / System" for SUPER_ADMIN,
              "Company" for TENANT_OWNER, "Department" for ADMIN. */}
          {quanTriGroups.map((group) => (
            <SidebarSection
              key={group.label}
              title={group.label}
              active={group.items.some((item) => pathname.startsWith(item.href))}
            >
              {group.items.map((item) => (
                <SidebarLink
                  key={item.href}
                  href={item.href}
                  icon={item.icon}
                  label={item.label}
                  active={pathname.startsWith(item.href)}
                />
              ))}
            </SidebarSection>
          ))}
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
    </>
  );
}

// Collapsible menu group: click the header to expand/collapse (chevron
// rotates to indicate state); a group containing the current route
// auto-expands (including re-expanding a group the user had collapsed, if
// navigation later lands inside it), matching the old nav's dropdown
// groups' behavior without reintroducing AppNav itself. Non-menu sections
// (agent switcher, recent chats) opt out via collapsible={false} and stay
// always-expanded exactly as before.
function SidebarSection({
  title,
  children,
  collapsible = true,
  active = false,
}: {
  title: string;
  children: React.ReactNode;
  collapsible?: boolean;
  active?: boolean;
}) {
  const [open, setOpen] = useState(active);
  useEffect(() => {
    if (active) setOpen(true);
  }, [active]);

  if (!collapsible) {
    return (
      <div className="mt-4">
        <div className="mb-1 px-2 text-[10px] font-semibold uppercase tracking-wider text-[var(--color-muted-fg)]">
          {title}
        </div>
        <div className="space-y-0.5">{children}</div>
      </div>
    );
  }

  return (
    <div className="mt-4">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex w-full items-center justify-between rounded px-2 py-1 text-[10px] font-semibold uppercase tracking-wider text-[var(--color-muted-fg)] transition hover:text-[var(--color-fg)]"
      >
        <span>{title}</span>
        <ChevronRight className={cn("size-3 shrink-0 transition-transform", open && "rotate-90")} />
      </button>
      {open && <div className="mt-1 space-y-0.5">{children}</div>}
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

// Agent avatar circle — auto-generated color from agentCode.
function AgentAvatar({ code, size = "md" }: { code: string; size?: "sm" | "md" }) {
  const { bg, fg, initial } = avatarForAgent(code);
  return <AgentAvatarByColors bg={bg} fg={fg} initial={initial} size={size} />;
}

function AgentAvatarByColors({
  bg,
  fg,
  initial,
  size = "md",
}: {
  bg: string;
  fg: string;
  initial: string;
  size?: "sm" | "md";
}) {
  const sizeClass = size === "sm" ? "size-4 text-[8px]" : "size-6 text-[10px]";
  return (
    <span
      className={cn(
        "flex shrink-0 items-center justify-center rounded-full font-bold",
        bg,
        fg,
        sizeClass,
      )}
    >
      {initial}
    </span>
  );
}
