import { describe, expect, it } from "vitest";
import { buildNghiepVuItems, buildQuanTriGroups, buildWorkspaceItems, canUseChat, type RoleFlags } from "./nav-model";

const role = (kind: "super" | "owner" | "admin" | "user", capabilityCodes: string[] = []): RoleFlags => ({
  isSuperAdmin: kind === "super",
  isTenantOwner: kind === "owner",
  isTenantAdmin: kind === "admin",
  capabilityCodes,
});
const hrefs = (items: { href: string }[]) => items.map((item) => item.href);
const groupLabels = (flags: RoleFlags) => buildQuanTriGroups(flags).map((group) => group.label);
const groupHrefs = (flags: RoleFlags) => buildQuanTriGroups(flags).flatMap((group) => hrefs(group.items));

describe("ACL V2-F exact navigation matrix", () => {
  it("SUPER_ADMIN sees only platform/system management, grouped, no chat/agents", () => {
    const flags = role("super");
    expect(buildWorkspaceItems(flags)).toEqual([]);
    expect(buildNghiepVuItems(flags)).toEqual([]);
    expect(canUseChat(flags)).toBe(false);
    expect(groupLabels(flags)).toEqual(["Platform / System", "Global AI Resources", "Operations"]);
    expect(groupHrefs(flags)).toEqual([
      "/admin/tenants", "/system/models", "/system/settings",
      "/admin/agent-builder", "/system/capabilities", "/system/workflows",
      "/system/skills", "/system/knowledge", "/admin/tools",
      "/system/usage", "/system/audit", "/admin/health",
    ]);
    // Tenant Owner management lives inside the Tenants screen, not a
    // separate menu entry.
    expect(groupHrefs(flags)).not.toContain("/system/tenant-owners");
  });

  it("TENANT_OWNER sees tenant governance without department operations or chat/agents", () => {
    const flags = role("owner");
    expect(buildWorkspaceItems(flags)).toEqual([]);
    expect(buildNghiepVuItems(flags)).toEqual([]);
    expect(canUseChat(flags)).toBe(false);
    expect(groupLabels(flags)).toEqual(["Tenant Management", "Tenant Operations"]);
    expect(groupHrefs(flags)).toEqual([
      "/admin/tenant-departments", "/tenant/users",
      "/tenant/usage", "/tenant/audit", "/tenant/reports",
    ]);
    // Department Admin management lives inside the Departments screen, not
    // a separate menu entry.
    expect(groupHrefs(flags)).not.toContain("/tenant/department-admins");
  });

  it("ADMIN gets the complete own-department AI Workspace and Department menus, plus chat", () => {
    const flags = role("admin", ["accounting.journal"]);
    expect(canUseChat(flags)).toBe(true);
    expect(hrefs(buildWorkspaceItems(flags))).toEqual([
      "/department/agents", "/department/capabilities", "/department/workflows",
      "/department/skills", "/department/knowledge", "/department/tools",
    ]);
    // User-facing label is "Nghiệp vụ", never "Năng lực" (internal code/API
    // naming stays "capabilities" — this is UI text only).
    expect(buildWorkspaceItems(flags).find((i) => i.href === "/department/capabilities")?.label)
      .toBe("Nghiệp vụ");
    expect(groupLabels(flags)).toEqual(["Department"]);
    expect(groupHrefs(flags)).toEqual([
      "/department/members", "/department/agent-assignments", "/department/review",
      "/department/reports", "/department/audit",
    ]);
    expect(groupHrefs(flags)).not.toContain("/admin/tenants");
  });

  it("USER gets no management menu and only capability-authorized business modules, plus chat", () => {
    const flags = role("user", ["accounting.tax", "contract.processing"]);
    expect(canUseChat(flags)).toBe(true);
    expect(buildWorkspaceItems(flags)).toEqual([]);
    expect(buildQuanTriGroups(flags)).toEqual([]);
    expect(hrefs(buildNghiepVuItems(flags))).toEqual(["/accounting", "/contracts"]);
    expect(hrefs(buildNghiepVuItems(role("user")))).toEqual([]);
  });

  it("ADMIN business menu resolves all 9 known capabilities to their exact pages, no duplicates", () => {
    const flags = role("admin", [
      "accounting.report", "accounting.journal", "accounting.tax", "accounting.invoice",
      "voucher.processing", "contract.processing",
      "customer.management", "supplier.management", "expense.processing",
    ]);
    const items = buildNghiepVuItems(flags);
    expect(hrefs(items)).toEqual([
      "/accounting", "/vouchers", "/contracts", "/customers", "/suppliers", "/expenses",
    ]);
    // Four accounting.* capabilities collapse to exactly one menu item.
    expect(items.filter((i) => i.href === "/accounting")).toHaveLength(1);
  });

  it("never exposes a role's forbidden operational families", () => {
    expect(groupHrefs(role("super")).some((href) => href.startsWith("/department/"))).toBe(false);
    expect(groupHrefs(role("owner")).some((href) => href.startsWith("/department/"))).toBe(false);
    expect(groupHrefs(role("admin")).some((href) => href.startsWith("/system/"))).toBe(false);
  });
});
