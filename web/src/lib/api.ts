// Server-side API client. Used by Next.js Route Handlers to proxy
// authenticated requests to Gateway + Agent without exposing tokens to
// the browser.

const GATEWAY_BASE = process.env.GATEWAY_BASE_URL ?? "http://aiq-gateway:8080";
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
};

export const agent = {
  chat(payload: {
    prompt: string;
    knowledge_context?: string;
    thread_id?: string;
    tool?: string | null;
    tool_arguments?: Record<string, unknown>;
    temperature?: number;
  }) {
    return call(AGENT_BASE, "/chat", {
      method: "POST",
      body: JSON.stringify(payload),
    });
  },
  listThreads(prefix: string, limit = 50) {
    const params = new URLSearchParams({ prefix, limit: String(limit) });
    return call(AGENT_BASE, `/chat/threads?${params.toString()}`);
  },
  runTask(payload: {
    task: string;
    user_token: string;
    skills?: string[];
    max_turns?: number;
  }) {
    return call(AGENT_BASE, "/agent/run", {
      method: "POST",
      body: JSON.stringify(payload),
    });
  },
  listSkills() {
    return call(AGENT_BASE, "/skills/list");
  },
  listAudit(limit = 100, event?: string) {
    const params = new URLSearchParams({ limit: String(limit) });
    if (event) params.set("event", event);
    return call(AGENT_BASE, `/admin/audit?${params.toString()}`);
  },
  queryEmbeddings(payload: {
    query: string;
    user_id: string;
    notes: { id: string; title: string; content: string }[];
    k?: number;
  }) {
    return call(AGENT_BASE, "/embeddings/query", {
      method: "POST",
      body: JSON.stringify(payload),
    });
  },
  getThread(threadId: string) {
    return call(AGENT_BASE, `/chat/threads/${encodeURIComponent(threadId)}`);
  },
  clearThread(threadId: string) {
    return call(AGENT_BASE, `/chat/threads/${encodeURIComponent(threadId)}`, {
      method: "DELETE",
    });
  },
};
