// Pure logic for the Chat / Agentic history separation (web/src/components/
// chat-room.tsx, web/src/app/api/chat/threads/route.ts, .../[id]/route.ts).
// Kept here — not inlined per-file — so the "never infer mode from title/
// content, only from the explicit persisted field" rule has exactly one
// implementation instead of being duplicated (and able to drift) across the
// component and the two API routes that used to each define their own copy.

export type ChatMode = "chat" | "agentic";

export interface ThreadModeRecord {
  mode: ChatMode | string;
}

export interface ThreadAgentRecord {
  agent_id?: string | null;
}

const CHAT_THREAD_STORAGE_KEY = "aiq.chat.thread_id";
const AGENTIC_THREAD_STORAGE_KEY = "aiq.chat.agentic_thread_id";

/** Sentinel used in place of a real agent id for the "no agent selected"
 * (plain/legacy chat) storage bucket — keeps that bucket distinct from any
 * real agent_id string, and stable across the session. */
const NO_AGENT_KEY = "_none";

/**
 * Explicit allowlist — anything that isn't literally "agentic" (including a
 * thread persisted before the `mode` field existed, or a malformed/missing
 * value from the wire) defaults to "chat". Never a heuristic over title or
 * message content.
 */
export function normalizeMode(raw: string | null | undefined): ChatMode {
  return raw === "agentic" ? "agentic" : "chat";
}

/** localStorage key holding the active-thread id for one mode. */
export function storageKeyForMode(mode: ChatMode): string {
  return mode === "agentic" ? AGENTIC_THREAD_STORAGE_KEY : CHAT_THREAD_STORAGE_KEY;
}

/**
 * localStorage key holding the active-thread id for one (agent, mode) pair.
 * Requirement: "localStorage/thread selection must also be agent-scoped" —
 * switching agents must never resume a DIFFERENT agent's in-progress thread
 * id, since that thread's prior_history would bleed a different agent's
 * earlier turns into the new agent's context. `agentId` is `null`/undefined
 * for the plain no-agent chat (super_admin/tenant_owner with nothing
 * selected) — that gets its own stable bucket, distinct from every real
 * agent id.
 */
export function storageKeyForAgentAndMode(
  agentId: string | null | undefined,
  mode: ChatMode,
): string {
  const base = storageKeyForMode(mode);
  return `${base}.agent.${agentId || NO_AGENT_KEY}`;
}

/**
 * Client-side guard mirroring the server-side `?mode=` filter — used so a
 * stale/mixed response can never leak the other mode's threads into the
 * sidebar, even if some future caller forgets to pass `mode` upstream.
 */
export function filterThreadsByMode<T extends ThreadModeRecord>(threads: T[], mode: ChatMode): T[] {
  return threads.filter((t) => normalizeMode(t.mode) === mode);
}

/**
 * The mode a history-item click should restore — always the item's OWN
 * persisted mode, never whatever mode the sidebar happened to be showing.
 * Named/exported on its own so "restore mode from the clicked item" is a
 * one-line, independently testable rule rather than logic buried inside a
 * larger click handler.
 */
export function modeForHistoryItem<T extends ThreadModeRecord>(item: T): ChatMode {
  return normalizeMode(item.mode);
}

/**
 * Client-side guard mirroring the server-side `?agent_id=` filter (belt and
 * braces alongside filterThreadsByMode) — an EXACT match only. A thread with
 * no stored agent_id (legacy, or created before this field existed) never
 * matches a specific `agentId`, even the caller's own currently-selected
 * one: requirement is "legacy threads without agent_id must not silently
 * attach to the currently selected agent."
 */
export function filterThreadsByAgent<T extends ThreadAgentRecord>(
  threads: T[],
  agentId: string | null | undefined,
): T[] {
  if (!agentId) return threads.filter((t) => !t.agent_id);
  return threads.filter((t) => t.agent_id === agentId);
}

/**
 * Whether a history item may be opened under the currently-selected agent
 * (requirement 7: opening a thread belonging to another agent must not
 * silently mix into the current agent's context). `currentAgentId` and the
 * item's own `agent_id` must match EXACTLY — including both being
 * null/undefined (plain no-agent chat opening another no-agent thread).
 */
export function threadBelongsToAgent<T extends ThreadAgentRecord>(
  item: T,
  currentAgentId: string | null | undefined,
): boolean {
  return (item.agent_id || null) === (currentAgentId || null);
}
