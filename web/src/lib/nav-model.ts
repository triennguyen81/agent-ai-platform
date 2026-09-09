import {
  Activity, Banknote, BarChart3, Bot, BookOpen, Building2, ClipboardCheck,
  FileText, Gauge, GraduationCap, HeartPulse, ListChecks, Puzzle,
  ScrollText, Settings, ShieldCheck, Truck, Users, Wrench, Workflow,
  type LucideIcon,
} from "lucide-react";

export interface NavItem { href: string; label: string; icon: LucideIcon }
export interface NavGroup { label: string; items: NavItem[] }
export interface RoleFlags {
  isSuperAdmin: boolean;
  isTenantOwner: boolean;
  isTenantAdmin: boolean;
  capabilityCodes?: readonly string[];
}

// Chat/agent-usage is a platform/tenant-governance-vs-operations split, not
// a per-page capability — SUPER_ADMIN and TENANT_OWNER don't operate AI
// Workspace resources day-to-day (they administer tenants/departments),
// so neither gets Chat, New chat, "Trợ lý của bạn", or recent chats.
export function canUseChat(flags: RoleFlags): boolean {
  return !flags.isSuperAdmin && !flags.isTenantOwner;
}

// Capability code -> business page. Multiple codes may resolve to the same
// page (all accounting.* codes share /accounting) but each page appears at
// most once in the result — order here is the menu order.
export function buildNghiepVuItems(flags: RoleFlags): NavItem[] {
  if (flags.isSuperAdmin || flags.isTenantOwner) return [];
  const codes = new Set(flags.capabilityCodes ?? []);
  const hasPrefix = (prefix: string) => [...codes].some((code) => code.startsWith(prefix));
  return [
    ...(hasPrefix("accounting.") ? [{ href: "/accounting", label: "Kế toán", icon: FileText }] : []),
    ...(codes.has("voucher.processing") ? [{ href: "/vouchers", label: "Chứng từ", icon: FileText }] : []),
    ...(codes.has("contract.processing") ? [{ href: "/contracts", label: "Hợp đồng", icon: ScrollText }] : []),
    ...(codes.has("customer.management") ? [{ href: "/customers", label: "Khách hàng", icon: Building2 }] : []),
    ...(codes.has("supplier.management") ? [{ href: "/suppliers", label: "Nhà cung cấp", icon: Truck }] : []),
    ...(codes.has("expense.processing") ? [{ href: "/expenses", label: "Chi phí", icon: Banknote }] : []),
  ];
}

export function buildWorkspaceItems(flags: RoleFlags): NavItem[] {
  if (!flags.isTenantAdmin) return [];
  return [
    { href: "/department/agents", label: "Agents", icon: Bot },
    { href: "/department/capabilities", label: "Nghiệp vụ", icon: ListChecks },
    { href: "/department/workflows", label: "Workflows", icon: Workflow },
    { href: "/department/skills", label: "Skills", icon: GraduationCap },
    { href: "/department/knowledge", label: "Knowledge", icon: BookOpen },
    { href: "/department/tools", label: "Tools", icon: Wrench },
  ];
}

export function buildQuanTriGroups(flags: RoleFlags): NavGroup[] {
  // Tenant Owner and Department Admin are managed *inside* their parent
  // screen (Tenant detail's "Tenant Owner" panel + transfer flow; a
  // Department's "Admins" tab) rather than as a separate top-level menu —
  // see docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §10 and
  // admin/tenants/[id]/page.tsx / admin/tenant-departments/[id]/page.tsx.
  if (flags.isSuperAdmin) return [
    {
      label: "Platform / System",
      items: [
        { href: "/admin/tenants", label: "Tenants", icon: Building2 },
        { href: "/system/models", label: "Models / Providers", icon: Gauge },
        { href: "/system/settings", label: "Settings", icon: Settings },
      ],
    },
    {
      label: "Global AI Resources",
      items: [
        { href: "/admin/agent-builder", label: "Agent Templates", icon: Puzzle },
        { href: "/system/capabilities", label: "Capabilities", icon: ListChecks },
        { href: "/system/workflows", label: "Workflows", icon: Workflow },
        { href: "/system/skills", label: "Skills", icon: GraduationCap },
        { href: "/system/knowledge", label: "Knowledge", icon: BookOpen },
        { href: "/admin/tools", label: "Tools", icon: Wrench },
      ],
    },
    {
      label: "Operations",
      items: [
        { href: "/system/usage", label: "Usage / Billing", icon: BarChart3 },
        { href: "/system/audit", label: "Platform Audit", icon: ShieldCheck },
        { href: "/admin/health", label: "Health", icon: HeartPulse },
      ],
    },
  ];
  if (flags.isTenantOwner) return [
    {
      label: "Tenant Management",
      items: [
        { href: "/admin/tenant-departments", label: "Departments", icon: Building2 },
        { href: "/tenant/users", label: "Users", icon: Users },
      ],
    },
    {
      label: "Tenant Operations",
      items: [
        { href: "/tenant/usage", label: "Usage", icon: Activity },
        { href: "/tenant/audit", label: "Audit", icon: ShieldCheck },
        { href: "/tenant/reports", label: "Reports", icon: BarChart3 },
      ],
    },
  ];
  if (flags.isTenantAdmin) return [{
    label: "Department",
    items: [
      { href: "/department/members", label: "Members", icon: Users },
      { href: "/department/agent-assignments", label: "Agent Assignments", icon: Bot },
      { href: "/department/review", label: "Review / HITL", icon: ClipboardCheck },
      { href: "/department/reports", label: "Reports", icon: BarChart3 },
      { href: "/department/audit", label: "Audit", icon: ShieldCheck },
    ],
  }];
  return [];
}
