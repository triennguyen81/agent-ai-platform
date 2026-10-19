// Server-side API client. Used by Next.js Route Handlers to proxy
// authenticated requests to Gateway + Agent without exposing tokens to
// the browser.

export const GATEWAY_BASE = process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080";
const AGENT_BASE = process.env.AGENT_BASE_URL ?? "http://aiq-agent:8000";

export type AuthResponse = {
  token: string;
  expiresAt: string;
  userId: string;
  email: string;
  roles: string[];
};

async function call(base: string, path: string, init: RequestInit = {}): Promise<Response> {
  const url = `${base}${path}`;
  return fetch(url, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(init.headers ?? {}),
    },
    cache: "no-store",
  });
}

export const gateway = {
  register(payload: { email: string; password: string; displayName?: string }) {
    return call(GATEWAY_BASE, "/auth/register", {
      method: "POST",
      body: JSON.stringify(payload),
    });
  },
  login(payload: { email: string; password: string }) {
    return call(GATEWAY_BASE, "/auth/login", {
      method: "POST",
      body: JSON.stringify(payload),
    });
  },
  me(token: string) {
    return call(GATEWAY_BASE, "/me", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  changePassword(token: string, payload: { currentPassword: string; newPassword: string; confirmPassword: string }) {
    return call(GATEWAY_BASE, "/me/change-password", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listNotes(token: string) {
    return call(GATEWAY_BASE, "/notes", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createNote(token: string, payload: { title: string; content: string; tags?: string[] }) {
    return call(GATEWAY_BASE, "/notes", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateNote(token: string, id: string, payload: { title?: string; content?: string; tags?: string[] }) {
    return call(GATEWAY_BASE, `/notes/${id}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deleteNote(token: string, id: string) {
    return call(GATEWAY_BASE, `/notes/${id}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listUsers(token: string) {
    return call(GATEWAY_BASE, "/users", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listContracts(token: string, params?: { status?: string; q?: string; limit?: number }) {
    const search = new URLSearchParams();
    if (params?.status) search.set("status", params.status);
    if (params?.q) search.set("q", params.q);
    if (params?.limit) search.set("limit", String(params.limit));
    const qs = search.toString();
    return call(GATEWAY_BASE, `/contracts${qs ? `?${qs}` : ""}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getContract(token: string, id: string) {
    return call(GATEWAY_BASE, `/contracts/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createContract(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/contracts", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateContract(token: string, id: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/contracts/${id}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deleteContract(token: string, id: string) {
    return call(GATEWAY_BASE, `/contracts/${id}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listContractDocuments(token: string,id: string){return call(GATEWAY_BASE,`/contracts/${id}/documents`,{headers:{Authorization:`Bearer ${token}`}});},
  uploadContractDocument(token:string,id:string,formData:FormData,documentType:string,referencedCode?:string){const q=new URLSearchParams({documentType});if(referencedCode)q.set("referencedCode",referencedCode);return fetch(`${GATEWAY_BASE}/contracts/${id}/documents?${q}`,{method:"POST",headers:{Authorization:`Bearer ${token}`},body:formData,cache:"no-store"});},
  deleteContractDocument(token:string,id:string,documentId:string){return call(GATEWAY_BASE,`/contracts/${id}/documents/${documentId}`,{method:"DELETE",headers:{Authorization:`Bearer ${token}`}});},
  reviewContract(token:string,id:string,payload:{approve:boolean;comment?:string}){return call(GATEWAY_BASE,`/contracts/${id}/review`,{method:"POST",headers:{Authorization:`Bearer ${token}`},body:JSON.stringify(payload)});},
  handoffContract(token:string,id:string){return call(GATEWAY_BASE,`/contracts/${id}/handoff`,{method:"POST",headers:{Authorization:`Bearer ${token}`}});},
  listChecklist(token: string, contractId: string) {
    return call(GATEWAY_BASE, `/contracts/${contractId}/checklist`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  bulkCreateChecklist(
    token: string,
    contractId: string,
    payload: { items: unknown[]; replaceExisting?: boolean },
  ) {
    return call(GATEWAY_BASE, `/contracts/${contractId}/checklist`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateChecklistItem(token: string, itemId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/checklist/${itemId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deleteChecklistItem(token: string, itemId: string) {
    return call(GATEWAY_BASE, `/checklist/${itemId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listNotifications(token: string, params?: { unread?: boolean; limit?: number }) {
    const search = new URLSearchParams();
    if (params?.unread) search.set("unread", "true");
    if (params?.limit) search.set("limit", String(params.limit));
    const qs = search.toString();
    return call(GATEWAY_BASE, `/notifications${qs ? `?${qs}` : ""}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  unreadNotificationCount(token: string) {
    return call(GATEWAY_BASE, "/notifications/unread-count", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  markNotificationRead(token: string, id: string) {
    return call(GATEWAY_BASE, `/notifications/${id}/read`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  markAllNotificationsRead(token: string) {
    return call(GATEWAY_BASE, "/notifications/mark-all-read", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Governance (G1) ----------
  listTenants(token: string) {
    return call(GATEWAY_BASE, "/tenants", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createTenant(token: string, payload: { code: string; name: string }) {
    return call(GATEWAY_BASE, "/tenants", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  onboardTenant(token: string, payload: {
    code: string;
    name: string;
    ownerDisplayName: string;
    ownerEmail: string;
    ownerPassword: string;
  }) {
    return call(GATEWAY_BASE, "/system/tenants/onboard", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listTenantSummaries(token: string) {
    return call(GATEWAY_BASE, "/system/tenants/summary", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getTenantSummary(token: string, tenantId: string) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/summary`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  transferTenantOwner(token: string, tenantId: string, payload: {
    successorUserId: string;
    predecessorAction: "demote" | "deactivate";
  }) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/owner/transfer`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getTenant(token: string, tenantId: string) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // SUPER_ADMIN: administrators of one tenant, created from the tenant
  // detail page. tenantId is a URL path segment (SUPER_ADMIN acts on a
  // tenant it does not itself belong to) — the server always forces
  // role=admin and this exact tenant on create.
  listTenantAdmins(token: string, tenantId: string) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/admins`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createTenantAdmin(token: string, tenantId: string, payload: { email: string; password: string; displayName?: string; departmentId: string }) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/admins`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  disableTenantAdmin(token: string, tenantId: string, userId: string) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/admins/${userId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  enableTenantAdmin(token: string, tenantId: string, userId: string) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/admins/${userId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // SUPER_ADMIN resetting a tenant ADMIN's password. Not yet wired to a UI
  // screen (same as disable/enable above — this whole route family has no
  // frontend caller today); kept for API parity and future use.
  resetTenantAdminPassword(token: string, tenantId: string, userId: string, payload: { newPassword: string; confirmPassword: string }) {
    return call(GATEWAY_BASE, `/system/tenants/${tenantId}/admins/${userId}/reset-password`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  // TENANT_OWNER: manage plain administrators in the authenticated tenant.
  // There is intentionally no tenantId or role argument.
  listOwnTenantAdmins(token: string) {
    return call(GATEWAY_BASE, "/tenant/admins", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createOwnTenantAdmin(token: string, payload: { email: string; password: string; displayName?: string; departmentId: string }) {
    return call(GATEWAY_BASE, "/tenant/admins", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  setOwnTenantAdminActive(token: string, userId: string, isActive: boolean) {
    return call(GATEWAY_BASE, `/tenant/admins/${userId}/${isActive ? "enable" : "disable"}`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // TENANT_OWNER resetting a plain ADMIN's password in their own tenant.
  resetOwnTenantAdminPassword(token: string, userId: string, payload: { newPassword: string; confirmPassword: string }) {
    return call(GATEWAY_BASE, `/tenant/admins/${userId}/reset-password`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateOwnTenantAdmin(token: string, userId: string, payload: { displayName?: string }) {
    return call(GATEWAY_BASE, `/tenant/admins/${userId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  // TENANT_OWNER-only USER<->ADMIN role flip for the unified tenant
  // accounts screen. Tenant is always derived server-side from the JWT.
  changeTenantUserRole(token: string, userId: string, role: "user" | "admin") {
    return call(GATEWAY_BASE, `/tenant/users/${userId}/role`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ role }),
    });
  },
  // D5 §9 — TENANT_OWNER-only department move for an ADMIN or USER. Tenant
  // is always derived server-side from the JWT, exactly like the role flip.
  changeTenantUserDepartment(token: string, userId: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/users/${userId}/department`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ departmentId }),
    });
  },
  getTenantQuota(token: string, tenantId: string) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}/quota`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateTenantQuota(token: string, tenantId: string, payload: { maxUsers: number | null; maxAgents: number | null }) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}/quota`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  // Tenant ADMIN: quota/status for their own tenant. Tenant is always
  // derived server-side from the JWT — there is no id parameter here.
  getMyTenantQuota(token: string) {
    return call(GATEWAY_BASE, "/tenant/quota", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // The authoritative "who are we" tax ID InvoiceDirectionResolver uses to
  // classify Sales vs Purchase. TENANT_OWNER-only (tenant_governance).
  // Tenant is always derived server-side from the JWT.
  getCompanyProfile(token: string) {
    return call(GATEWAY_BASE, "/tenant/company-profile", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateCompanyProfile(token: string, payload: { companyTaxId: string | null }) {
    return call(GATEWAY_BASE, "/tenant/company-profile", {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
  },
  // "Hồ sơ doanh nghiệp" — AIQ's own legal-entity identity for the tenant
  // ("công ty chủ thể"), a separate, broader record from getCompanyProfile
  // above (which is only the single tax-code field InvoiceDirectionResolver
  // uses). TENANT_OWNER-only (tenant_governance); tenant is always derived
  // server-side from the JWT, never sent here.
  getBusinessProfile(token: string) {
    return call(GATEWAY_BASE, "/tenant/business-profile", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateBusinessProfile(token: string, payload: {
    legalName: string | null; taxCode: string | null; shortName: string | null;
    aliases: string[]; registeredAddress: string | null; legalRepresentative: string | null;
    representativeTitle: string | null; phone: string | null; email: string | null; website: string | null;
  }) {
    return call(GATEWAY_BASE, "/tenant/business-profile", {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
  },
  listTenantMembers(token: string, tenantId: string) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}/members`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  inviteTenantMember(token: string, tenantId: string, payload: { userId: string; role: string }) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}/members`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  removeTenantMember(token: string, tenantId: string, userId: string) {
    return call(GATEWAY_BASE, `/tenants/${tenantId}/members/${userId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listMyTenants(token: string) {
    return call(GATEWAY_BASE, "/me/tenants", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  switchActiveTenant(token: string, tenantId: string) {
    return call(GATEWAY_BASE, "/me/tenants/switch", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ tenantId }),
    });
  },
  listAgentKnowledge(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/knowledge`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAttachableNotes(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/knowledge/attachable`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  attachNoteToAgent(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/knowledge`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ noteId }),
    });
  },
  detachNoteFromAgent(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/knowledge/${noteId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  agentKnowledgeContext(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/knowledge/context`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAgentsForUser(token: string, userId: string) {
    return call(GATEWAY_BASE, `/users/${userId}/agents`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listMyAgents(token: string) {
    return call(GATEWAY_BASE, "/me/agents", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAgents(token: string, state?: string) {
    const qs = state ? `?state=${encodeURIComponent(state)}` : "";
    return call(GATEWAY_BASE, `/agents${qs}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createAgent(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/agents", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateAgent(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/agents/${agentId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  transitionAgent(token: string, agentId: string, payload: { toState: string; reason?: string }) {
    return call(GATEWAY_BASE, `/agents/${agentId}/transitions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  disableAgent(token: string, agentId: string, reason?: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ reason: reason ?? null }),
    });
  },
  enableAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAgentLifecycle(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/lifecycle`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAgentAcl(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/acl`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  grantAgentAcl(token: string, agentId: string, payload: { principalId: string; accessLevel: string }) {
    return call(GATEWAY_BASE, `/agents/${agentId}/acl`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  revokeAgentAcl(token: string, agentId: string, principalId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/acl/${principalId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Tool Registry (G2) ----------
  listTools(token: string, activeOnly?: boolean) {
    const qs = activeOnly ? "?activeOnly=true" : "";
    return call(GATEWAY_BASE, `/tools${qs}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createTool(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/tools", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateTool(token: string, toolId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/tools/${toolId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listAgentTools(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/tools`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  grantAgentTool(token: string, agentId: string, toolId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/tools`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ toolId }),
    });
  },
  revokeAgentTool(token: string, agentId: string, toolId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/tools/${toolId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- end Tool Registry ----------
  // ---------- Review Board (G3) ----------
  listReviewBoards(token: string) {
    return call(GATEWAY_BASE, "/review-boards", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createReviewBoard(token: string, payload: { name: string; quorum?: number }) {
    return call(GATEWAY_BASE, "/review-boards", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listBoardMembers(token: string, boardId: string) {
    return call(GATEWAY_BASE, `/review-boards/${boardId}/members`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  addBoardMember(token: string, boardId: string, payload: { userId: string; role?: string }) {
    return call(GATEWAY_BASE, `/review-boards/${boardId}/members`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  removeBoardMember(token: string, boardId: string, userId: string) {
    return call(GATEWAY_BASE, `/review-boards/${boardId}/members/${userId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAgentReviewRequests(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/review-requests`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  submitReviewRequest(token: string, agentId: string, payload: { boardId: string; rationale?: string }) {
    return call(GATEWAY_BASE, `/agents/${agentId}/review-requests`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getReviewRequest(token: string, requestId: string) {
    return call(GATEWAY_BASE, `/review-requests/${requestId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listReviewVotes(token: string, requestId: string) {
    return call(GATEWAY_BASE, `/review-requests/${requestId}/votes`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  castReviewVote(token: string, requestId: string, payload: { vote: string; comment?: string }) {
    return call(GATEWAY_BASE, `/review-requests/${requestId}/votes`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  myReviewQueue(token: string) {
    return call(GATEWAY_BASE, "/me/review-queue", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- end Review Board ----------
  // ---------- HITL (G4) ----------
  listAgentGates(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/agents/${agentId}/gates`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createAgentGate(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/agents/${agentId}/gates`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateGate(token: string, gateId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/gates/${gateId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deleteGate(token: string, gateId: string) {
    return call(GATEWAY_BASE, `/gates/${gateId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createHitlAction(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/hitl/actions", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  hitlQueue(token: string) {
    return call(GATEWAY_BASE, "/hitl/queue", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  decideHitlAction(token: string, actionId: string, payload: { decision: string; comment?: string }) {
    return call(GATEWAY_BASE, `/hitl/${actionId}/decide`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  // ---------- end HITL ----------
  // ---------- System Agent Builder (Phase 4D, SUPER_ADMIN only) ----------
  listSystemAgents(token: string) {
    return call(GATEWAY_BASE, "/system/agents", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createSystemAgent(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/system/agents", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getSystemAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateSystemAgent(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  enableSystemAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  disableSystemAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listSystemAgentVersions(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getSystemAgentVersion(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions/${versionId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createSystemAgentVersion(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateSystemAgentVersion(
    token: string,
    agentId: string,
    versionId: string,
    payload: Record<string, unknown>,
  ) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions/${versionId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  validateSystemAgentVersion(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions/${versionId}/validate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  publishSystemAgentVersion(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/versions/${versionId}/publish`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Department Agent Builder (ACL V2-C, ADMIN's own department) ----------
  // Mirrors the System Agent Builder calls above one-for-one — same request/
  // response shapes, `/department/agents` instead of `/system/agents`.
  // Tenant/department are always derived server-side; never passed here.
  // Named distinctly from the pre-existing listDepartmentAgents(token,
  // departmentId) below (D5's TENANT_OWNER department-entitlement view,
  // GET /tenant/departments/{departmentId}/agents) — this one is ADMIN's own
  // CRUD-owned agents at GET /department/agents, a different resource set.
  listOwnDepartmentAgents(token: string) {
    return call(GATEWAY_BASE, "/department/agents", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createDepartmentAgent(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/department/agents", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getDepartmentAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateDepartmentAgent(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  enableDepartmentAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  disableDepartmentAgent(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listDepartmentAgentVersions(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createDepartmentAgentVersion(token: string, agentId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateDepartmentAgentVersion(
    token: string,
    agentId: string,
    versionId: string,
    payload: Record<string, unknown>,
  ) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions/${versionId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  // Configurable document processing (versioned with the Agent). Edits only
  // ever target a DRAFT version; the Gateway rejects edits to published ones.
  listDepartmentDocumentTypes(token: string) {
    return call(GATEWAY_BASE, `/department/document-types`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getDepartmentAgentDocumentProcessing(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions/${versionId}/document-processing`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  replaceDepartmentAgentDocumentProcessing(token: string, agentId: string, versionId: string, types: unknown[]) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions/${versionId}/document-processing`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ types }),
    });
  },
  testDepartmentAgentDocumentProcessing(
    token: string,
    agentId: string,
    payload: { rawText?: string; voucherId?: string; voucherTypeHint?: string },
  ) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/document-processing/test`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  validateDepartmentAgentVersion(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions/${versionId}/validate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  publishDepartmentAgentVersion(token: string, agentId: string, versionId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/versions/${versionId}/publish`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listDepartmentAgentUsers(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/users`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  assignDepartmentAgentUser(token: string, agentId: string, userId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/users/${userId}`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  revokeDepartmentAgentUser(token: string, agentId: string, userId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/users/${userId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Department Business Capabilities / Nghiệp vụ (ACL V2-D) ----------
  // ADMIN's own Department capability CRUD + operational binding. Tenant/
  // department always derived server-side; never passed here.
  listOwnDepartmentCapabilities(token: string) {
    return call(GATEWAY_BASE, "/department/capabilities", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createDepartmentCapability(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/department/capabilities", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getDepartmentCapability(token: string, capabilityId: string) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateDepartmentCapability(token: string, capabilityId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  enableDepartmentCapability(token: string, capabilityId: string) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  disableDepartmentCapability(token: string, capabilityId: string) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Own-department binding-management view (same shape as the owner's
  // oversight-only listDepartmentCapabilities below, principal-scoped).
  listOwnDepartmentCapabilityBindings(token: string) {
    return call(GATEWAY_BASE, "/department/capabilities/bindings", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  setOwnDepartmentCapabilityBinding(token: string, capabilityId: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}/binding`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ agentId }),
    });
  },
  revokeOwnDepartmentCapabilityBinding(token: string, capabilityId: string) {
    return call(GATEWAY_BASE, `/department/capabilities/${capabilityId}/binding`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Department Workflow / Skill / Knowledge (ACL V2-E) ----------
  // ADMIN's own Department Workflow CRUD + immutable draft/publish versioning.
  listOwnDepartmentWorkflows(token: string) {
    return call(GATEWAY_BASE, "/department/workflows", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createDepartmentWorkflow(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/department/workflows", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getDepartmentWorkflow(token: string, workflowId: string) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  updateDepartmentWorkflowMetadata(token: string, workflowId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  enableDepartmentWorkflow(token: string, workflowId: string) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  disableDepartmentWorkflow(token: string, workflowId: string) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listDepartmentWorkflowVersions(token: string, workflowId: string) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/versions`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createDepartmentWorkflowDraftVersion(token: string, workflowId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/versions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateDepartmentWorkflowDraftVersion(token: string, workflowId: string, versionId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/versions/${versionId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  publishDepartmentWorkflowVersion(token: string, workflowId: string, versionId: string) {
    return call(GATEWAY_BASE, `/department/workflows/${workflowId}/versions/${versionId}/publish`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ADMIN's own Department Skill CRUD (declarative instruction text only).
  listOwnDepartmentSkills(token: string) {
    return call(GATEWAY_BASE, "/department/skills", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listPlatformSkills(token: string) { return call(GATEWAY_BASE, "/system/skills", { headers: { Authorization: `Bearer ${token}` } }); },
  createPlatformSkill(token: string, payload: Record<string, unknown>) { return call(GATEWAY_BASE, "/system/skills", { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload) }); },
  updatePlatformSkillContent(token: string, skillId: string, payload: Record<string, unknown>) { return call(GATEWAY_BASE, `/system/skills/${skillId}/content`, { method: "PATCH", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload) }); },
  setPlatformSkillEnabled(token: string, skillId: string, enabled: boolean) { return call(GATEWAY_BASE, `/system/skills/${skillId}/${enabled ? "enable" : "disable"}`, { method: "POST", headers: { Authorization: `Bearer ${token}` } }); },
  createDepartmentSkill(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/department/skills", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateDepartmentSkillMetadata(token: string, skillId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/skills/${skillId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateDepartmentSkillContent(token: string, skillId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/skills/${skillId}/content`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  enableDepartmentSkill(token: string, skillId: string) {
    return call(GATEWAY_BASE, `/department/skills/${skillId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  disableDepartmentSkill(token: string, skillId: string) {
    return call(GATEWAY_BASE, `/department/skills/${skillId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ADMIN's own Department Knowledge CRUD (plain text, reuses Notes' scope='department' rows).
  listOwnDepartmentKnowledge(token: string) {
    return call(GATEWAY_BASE, "/department/knowledge", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listPlatformKnowledge(token: string) { return call(GATEWAY_BASE, "/system/knowledge", { headers: { Authorization: `Bearer ${token}` } }); },
  createPlatformKnowledge(token: string, payload: Record<string, unknown>) { return call(GATEWAY_BASE, "/system/knowledge", { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload) }); },
  archivePlatformKnowledge(token: string, noteId: string) { return call(GATEWAY_BASE, `/system/knowledge/${noteId}/archive`, { method: "POST", headers: { Authorization: `Bearer ${token}` } }); },
  deletePlatformKnowledge(token: string, noteId: string) { return call(GATEWAY_BASE, `/system/knowledge/${noteId}`, { method: "DELETE", headers: { Authorization: `Bearer ${token}` } }); },
  createDepartmentKnowledge(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/department/knowledge", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateDepartmentKnowledge(token: string, noteId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/department/knowledge/${noteId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  archiveDepartmentKnowledge(token: string, noteId: string) {
    return call(GATEWAY_BASE, `/department/knowledge/${noteId}/archive`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  restoreDepartmentKnowledge(token: string, noteId: string) {
    return call(GATEWAY_BASE, `/department/knowledge/${noteId}/restore`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  deleteDepartmentKnowledge(token: string, noteId: string) {
    return call(GATEWAY_BASE, `/department/knowledge/${noteId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Attach/detach own Department Knowledge to own Department Agent (mapped under DepartmentAgentEndpoints).
  listDepartmentAgentKnowledge(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/knowledge`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAttachableDepartmentAgentKnowledge(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/knowledge/attachable`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  attachDepartmentAgentKnowledge(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/knowledge/${noteId}`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  detachDepartmentAgentKnowledge(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/department/agents/${agentId}/knowledge/${noteId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Tenant/User Agent Assignment (Phase 4E) ----------
  // SUPER_ADMIN: assign a published system agent to tenants.
  listSystemAgentTenants(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/tenants`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  assignSystemAgentTenant(token: string, agentId: string, tenantId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/tenants/${tenantId}`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  revokeSystemAgentTenant(token: string, agentId: string, tenantId: string) {
    return call(GATEWAY_BASE, `/system/agents/${agentId}/tenants/${tenantId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Tenant ADMIN: agents assigned to my own tenant, and my tenant's users.
  // Tenant is always derived server-side from the JWT — never passed here.
  listTenantAgents(token: string) {
    return call(GATEWAY_BASE, "/tenant/agents", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listTenantUsers(token: string) {
    return call(GATEWAY_BASE, "/tenant/users", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listTenantDepartments(token: string) {
    return call(GATEWAY_BASE, "/tenant/departments", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // D5 — department CRUD. GET is reachable by admin (own department only,
  // enforced server-side); the rest are tenant_owner-only.
  getTenantDepartment(token: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createTenantDepartment(token: string, payload: { code: string; name: string }) {
    return call(GATEWAY_BASE, "/tenant/departments", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateTenantDepartment(token: string, departmentId: string, payload: { name: string }) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deactivateTenantDepartment(token: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/deactivate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  activateTenantDepartment(token: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/activate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createTenantUser(token: string, payload: { email: string; password: string; displayName?: string; departmentId?: string }) {
    return call(GATEWAY_BASE, "/tenant/users", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateTenantUser(token: string, userId: string, payload: { displayName?: string }) {
    return call(GATEWAY_BASE, `/tenant/users/${userId}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  disableTenantUser(token: string, userId: string) {
    return call(GATEWAY_BASE, `/tenant/users/${userId}/disable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  enableTenantUser(token: string, userId: string) {
    return call(GATEWAY_BASE, `/tenant/users/${userId}/enable`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ADMIN (own department) / TENANT_OWNER (own tenant) resetting a
  // role='user' account's password. Never touches admin/tenant_owner rows
  // — see resetOwnTenantAdminPassword / resetTenantAdminPassword below.
  resetTenantUserPassword(token: string, userId: string, payload: { newPassword: string; confirmPassword: string }) {
    return call(GATEWAY_BASE, `/tenant/users/${userId}/reset-password`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listTenantAgentUsers(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/users`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  assignTenantAgentUser(token: string, agentId: string, userId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/users/${userId}`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  revokeTenantAgentUser(token: string, agentId: string, userId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/users/${userId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listTenantAgentKnowledge(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/knowledge`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listAttachableTenantAgentKnowledge(token: string, agentId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/knowledge/attachable`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  attachTenantAgentKnowledge(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/knowledge/${noteId}`, {
      method: "PUT", headers: { Authorization: `Bearer ${token}` },
    });
  },
  detachTenantAgentKnowledge(token: string, agentId: string, noteId: string) {
    return call(GATEWAY_BASE, `/tenant/agents/${agentId}/knowledge/${noteId}`, {
      method: "DELETE", headers: { Authorization: `Bearer ${token}` },
    });
  },
  // D3 — TENANT_OWNER-only department-agent entitlement. departmentId comes
  // from the caller's own selection in the UI, but authorization is always
  // re-derived server-side from the tenant_owner's session tenant, never
  // trusted from this call alone.
  listDepartmentAgents(token: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/agents`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  assignDepartmentAgent(token: string, departmentId: string, agentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/agents/${agentId}`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  revokeDepartmentAgent(token: string, departmentId: string, agentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/agents/${agentId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // D4 — business capability ("nghiệp vụ") catalog, visibility, and
  // TENANT_OWNER-only department capability-to-agent binding.
  listCapabilityCatalog(token: string) {
    return call(GATEWAY_BASE, "/system/capabilities", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createPlatformCapability(token: string, payload: Record<string, unknown>) { return call(GATEWAY_BASE, "/system/capabilities", { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload) }); },
  setPlatformCapabilityEnabled(token: string, capabilityId: string, enabled: boolean) { return call(GATEWAY_BASE, `/system/capabilities/${capabilityId}/${enabled ? "enable" : "disable"}`, { method: "POST", headers: { Authorization: `Bearer ${token}` } }); },
  listMyCapabilities(token: string) {
    return call(GATEWAY_BASE, "/me/capabilities", {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  resolveCapabilityAgent(token: string, code: string) {
    return call(GATEWAY_BASE, `/me/capabilities/${encodeURIComponent(code)}/agent`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // "Xử lý bằng trợ lý" selector data: every agent this principal could be
  // routed to for one capability (USER: own assignments only; ADMIN: own
  // department per current ACL V2). Never itself an authorization decision
  // — the chosen id still goes through resolveCapabilityAgent's/
  // runTask's server-side re-validation.
  listEligibleCapabilityAgents(token: string, code: string) {
    return call(GATEWAY_BASE, `/me/capabilities/${encodeURIComponent(code)}/agents`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // TENANT_OWNER's read-only oversight of a department's capability
  // landscape (ACL V2-D: binding mutation moved to ADMIN — see
  // setOwnDepartmentCapabilityBinding/revokeOwnDepartmentCapabilityBinding
  // above — this stays GET-only).
  listDepartmentCapabilities(token: string, departmentId: string) {
    return call(GATEWAY_BASE, `/tenant/departments/${departmentId}/capabilities`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listWorkflows(token: string, activeOnly = false) {
    return call(GATEWAY_BASE, `/workflows?activeOnly=${activeOnly}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createWorkflow(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/workflows", {
      method: "POST", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
    });
  },
  updateWorkflow(token: string, workflowId: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/workflows/${workflowId}`, {
      method: "PATCH", headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
    });
  },
  // ---------- end Tenant/User Agent Assignment ----------
  // ---------- end System Agent Builder ----------
  // ---------- end Governance ----------
  assignRole(token: string, userId: string, role: string) {
    return call(GATEWAY_BASE, `/users/${userId}/roles`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ role }),
    });
  },
  removeRole(token: string, userId: string, role: string) {
    return call(GATEWAY_BASE, `/users/${userId}/roles/${role}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Vouchers (kế toán chứng từ) ----------
  listVouchers(
    token: string,
    params?: {
      voucherType?: string;
      status?: string;
      q?: string;
      sellerMst?: string;
      documentDateFrom?: string;
      documentDateTo?: string;
      limit?: number;
      offset?: number;
    },
  ) {
    const search = new URLSearchParams();
    if (params?.voucherType) search.set("voucherType", params.voucherType);
    if (params?.status) search.set("status", params.status);
    if (params?.q) search.set("q", params.q);
    if (params?.sellerMst) search.set("sellerMst", params.sellerMst);
    if (params?.documentDateFrom) search.set("documentDateFrom", params.documentDateFrom);
    if (params?.documentDateTo) search.set("documentDateTo", params.documentDateTo);
    if (params?.limit) search.set("limit", String(params.limit));
    if (params?.offset) search.set("offset", String(params.offset));
    const qs = search.toString();
    return call(GATEWAY_BASE, `/vouchers${qs ? `?${qs}` : ""}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getVoucherProcessingState(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/processing-state`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listVoucherDocumentReviews(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/document-review`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getVoucher(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Chứng từ THU / CHI — direction, counterparty, open AR/AP candidates,
  // suggested match and current posting (GET /vouchers/{id}/settlement).
  getSettlement(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/settlement`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getReceiptMatch(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/receipt-match`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  rejectReceiptMatch(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/reject-receipt-match`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getExpenseClaimResult(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/expense-claim-result`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  rejectExpenseClaim(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/reject-expense-claim`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  grantExpenseException(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}/grant-expense-exception`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createVoucher(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/vouchers", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateVoucher(token: string, id: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/vouchers/${id}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deleteVoucher(token: string, id: string) {
    return call(GATEWAY_BASE, `/vouchers/${id}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  saveVoucherJournal(
    token: string,
    id: string,
    payload: { entries: unknown[]; replaceExisting?: boolean },
  ) {
    return call(GATEWAY_BASE, `/vouchers/${id}/journal-suggestions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  decideVoucherJournal(
    token: string,
    id: string,
    entryId: string,
    payload: { accept: boolean; rejectReason?: string },
  ) {
    return call(
      GATEWAY_BASE,
      `/vouchers/${id}/journal-suggestions/${entryId}/decision`,
      {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
        body: JSON.stringify(payload),
      },
    );
  },
  uploadVoucher(token: string, formData: FormData, voucherType: string, preferredAgentId?: string) {
    // multipart — do not set Content-Type manually. preferredAgentId is a
    // hint only: the Gateway re-authorizes it in the extract job.
    const params: Record<string, string> = { voucherType };
    if (preferredAgentId) params.preferredAgentId = preferredAgentId;
    const qs = new URLSearchParams(params).toString();
    return fetch(`${GATEWAY_BASE}/vouchers/upload?${qs}`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: formData,
      cache: "no-store",
    });
  },
  // Contract Upload page: upload a PDF/DOCX/PNG/JPG, get extracted text
  // back — never creates a contract by itself.
  extractContractText(token: string, formData: FormData) {
    // multipart — do not set Content-Type manually.
    return fetch(`${GATEWAY_BASE}/contracts/extract-text`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: formData,
      cache: "no-store",
    });
  },
  // Accounting Invoice page: upload a PDF/DOCX/PNG/JPG/XML, get extracted
  // text back — never creates/applies any accounting mutation.
  extractInvoiceText(token: string, formData: FormData) {
    // multipart — do not set Content-Type manually.
    return fetch(`${GATEWAY_BASE}/accounting/invoice/extract-text`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: formData,
      cache: "no-store",
    });
  },
  customerByMst(token: string, mst: string) {
    return call(GATEWAY_BASE, `/customers/by-mst/${encodeURIComponent(mst)}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Bank Account Master (owner = "customers" | "suppliers").
  listBankAccounts(token: string, owner: "customers" | "suppliers", ownerId: string) {
    return call(GATEWAY_BASE, `/${owner}/${ownerId}/bank-accounts`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // MY_COMPANY's own bank accounts (existing sim_bank_accounts), read-only.
  listCompanyBankAccounts(token: string) {
    return call(GATEWAY_BASE, `/vouchers/company-bank-accounts`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // Bank statement lines + Customer identification for a bank_statement voucher.
  voucherBankTransactions(token: string, voucherId: string) {
    return call(GATEWAY_BASE, `/vouchers/${voucherId}/bank-transactions`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  customerReceivables(token: string, customerId: string) {
    return call(GATEWAY_BASE, `/customers/${customerId}/receivables`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  salesOrderByInvoice(token: string, documentNumber: string) {
    return call(GATEWAY_BASE, `/sales-orders/by-invoice/${encodeURIComponent(documentNumber)}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  listCustomers(token: string, params?: { category?: string; q?: string; includeInactive?: boolean }) {
    const qs = new URLSearchParams();
    if (params?.category) qs.set("category", params.category);
    if (params?.q) qs.set("q", params.q);
    if (params?.includeInactive) qs.set("includeInactive", "true");
    const url = qs.toString() ? `/customers/?${qs}` : "/customers/";
    return call(GATEWAY_BASE, url, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getCustomer(token: string, id: string) {
    return call(GATEWAY_BASE, `/customers/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createCustomer(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/customers", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateCustomer(token: string, id: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/customers/${id}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deactivateCustomer(token: string, id: string) {
    return call(GATEWAY_BASE, `/customers/${id}/deactivate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  reactivateCustomer(token: string, id: string) {
    return call(GATEWAY_BASE, `/customers/${id}/reactivate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Suppliers (UC3 AP side) ----------
  listSuppliers(token: string, params?: { country?: string; q?: string; includeInactive?: boolean }) {
    const qs = new URLSearchParams();
    if (params?.country) qs.set("country", params.country);
    if (params?.q) qs.set("q", params.q);
    if (params?.includeInactive) qs.set("includeInactive", "true");
    const url = qs.toString() ? `/suppliers/?${qs}` : "/suppliers/";
    return call(GATEWAY_BASE, url, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getSupplier(token: string, id: string) {
    return call(GATEWAY_BASE, `/suppliers/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  createSupplier(token: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, "/suppliers", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  updateSupplier(token: string, id: string, payload: Record<string, unknown>) {
    return call(GATEWAY_BASE, `/suppliers/${id}`, {
      method: "PATCH",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  deactivateSupplier(token: string, id: string) {
    return call(GATEWAY_BASE, `/suppliers/${id}/deactivate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  reactivateSupplier(token: string, id: string) {
    return call(GATEWAY_BASE, `/suppliers/${id}/reactivate`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  supplierByMst(token: string, mst: string) {
    return call(GATEWAY_BASE, `/suppliers/by-mst/${encodeURIComponent(mst)}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  supplierPayables(token: string, supplierId: string) {
    return call(GATEWAY_BASE, `/suppliers/${supplierId}/payables`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  apSummary(token: string) {
    return call(GATEWAY_BASE, "/suppliers/ap-summary", {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  purchaseOrderByInvoice(token: string, documentNumber: string) {
    return call(GATEWAY_BASE, `/purchase-orders/by-invoice/${encodeURIComponent(documentNumber)}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  // ---------- Sprint 5 HITL Approval Queue ----------
  listApprovalQueue(token: string, limit = 50) {
    return call(GATEWAY_BASE, `/approval-queue/?limit=${limit}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  getApprovalItem(token: string, id: string) {
    return call(GATEWAY_BASE, `/approval-queue/${id}`, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  reviewApprovalItem(token: string, id: string, decision: "approved" | "rejected", note?: string) {
    return call(GATEWAY_BASE, `/approval-queue/${id}/review`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ decision, note: note ?? null }),
    });
  },
};

export const agent = {
  chat(token: string, payload: {
    prompt: string;
    knowledge_context?: string;
    thread_id?: string;
    tool?: string | null;
    tool_arguments?: Record<string, unknown>;
    temperature?: number;
    agent_id?: string;
    // ACL V2-C: Department Agent Builder's Test action — see ChatRequest.test
    // in agent/src/aiq_agent/api/schemas.py.
    test?: boolean;
  }) {
    return call(AGENT_BASE, "/chat", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listThreads(
    token: string,
    prefix: string,
    limit = 50,
    mode?: "chat" | "agentic",
    agentId?: string | null,
  ) {
    const params = new URLSearchParams({ prefix, limit: String(limit) });
    if (mode) params.set("mode", mode);
    if (agentId) params.set("agent_id", agentId);
    return call(AGENT_BASE, `/chat/threads?${params.toString()}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  runTask(token: string, payload: {
    task: string;
    agent_id?: string;
    capability?: string;
    // "Xử lý bằng trợ lý" selector — only meaningful together with
    // `capability`; the Gateway re-validates it against a freshly computed
    // eligible set before ever honoring it (see resolve_capability_agent).
    preferred_agent_id?: string;
    skills?: string[];
    max_turns?: number;
    thread_id?: string;
  }) {
    return call(AGENT_BASE, "/agent/run", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  listSkills() {
    return call(AGENT_BASE, "/skills/list");
  },
  listAudit(token: string, limit = 100, event?: string) {
    const params = new URLSearchParams({ limit: String(limit) });
    if (event) params.set("event", event);
    return call(AGENT_BASE, `/admin/audit?${params.toString()}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  },
  queryEmbeddings(token: string, payload: {
    query: string;
    user_id: string;
    notes: { id: string; title: string; content: string }[];
    k?: number;
  }) {
    return call(AGENT_BASE, "/embeddings/query", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify(payload),
    });
  },
  getThread(token: string, threadId: string, agentId?: string | null) {
    const params = new URLSearchParams();
    if (agentId) params.set("agent_id", agentId);
    const qs = params.toString();
    return call(
      AGENT_BASE,
      `/chat/threads/${encodeURIComponent(threadId)}${qs ? `?${qs}` : ""}`,
      { headers: { Authorization: `Bearer ${token}` } },
    );
  },
  clearThread(token: string, threadId: string) {
    return call(AGENT_BASE, `/chat/threads/${encodeURIComponent(threadId)}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  },
};
