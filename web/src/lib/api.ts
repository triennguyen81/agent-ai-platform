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
  getThread(threadId: string) {
    return call(AGENT_BASE, `/chat/threads/${encodeURIComponent(threadId)}`);
  },
  clearThread(threadId: string) {
    return call(AGENT_BASE, `/chat/threads/${encodeURIComponent(threadId)}`, {
      method: "DELETE",
    });
  },
};
