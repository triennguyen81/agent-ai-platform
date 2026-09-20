"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Brain,
  Bot,
  Check,
  Copy,
  Link as LinkIcon,
  Loader2,
  MessageSquare,
  Plus,
  Send,
  Sparkles,
  Trash2,
  User as UserIcon,
  Wand2,
  Zap,
  ZapOff,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { MicButton } from "@/components/mic-button";
import { SaveToKbButton } from "@/components/save-to-kb-button";
import {
  filterThreadsByAgent,
  filterThreadsByMode,
  modeForHistoryItem,
  normalizeMode,
  storageKeyForAgentAndMode,
  threadBelongsToAgent,
} from "@/lib/chat-history";

interface AgenticStep {
  role: string;
  text: string;
}

interface Message {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  thinking?: string | null;
  question?: string;
  meta?: { provider?: string; model?: string };
  /** Set when this came from /agent/run; UI shows a collapsible trace. */
  agenticSteps?: AgenticStep[];
}

interface ChatApiResponse {
  final_answer: string;
  provider: string;
  model: string;
  thread_id?: string | null;
  thinking?: string | null;
  steps?: unknown[];
}

interface AgentRunApiResponse {
  final_answer: string;
  steps: AgenticStep[];
}

type ChatMode = "chat" | "agentic";

const URL_RE = /\bhttps?:\/\/[^\s<>"']+/gi;

function findUrls(text: string): string[] {
  const matches = text.match(URL_RE) ?? [];
  return Array.from(new Set(matches));
}

// Chat and Agentic each keep their own active-thread pointer, scoped to the
// currently selected agent — switching the mode toggle OR the agent must
// never mix histories or clobber another (mode, agent) pair's in-progress
// thread. See @/lib/chat-history for the localStorage key selection
// (storageKeyForAgentAndMode) and mode/agent-filtering logic shared with the
// /api/chat/threads routes.
const AUTO_SUBMIT_KEY = "aiq.chat.mic.autoSubmit";

const THINKING_TAG_RE = /<thinking>[\s\S]*?<\/thinking>/gi;
const ORPHAN_OPEN_RE = /<thinking>/gi;
const ORPHAN_CLOSE_RE = /<\/thinking>/gi;

function extractThinking(raw: string, existing?: string | null): { thinking: string | null; answer: string } {
  const matches = raw.match(THINKING_TAG_RE) ?? [];
  let answer = raw.replace(THINKING_TAG_RE, "");
  answer = answer.replace(ORPHAN_OPEN_RE, "").replace(ORPHAN_CLOSE_RE, "").trim();
  const extracted = matches
    .map((m) => m.replace(/^<thinking>/i, "").replace(/<\/thinking>$/i, "").trim())
    .filter(Boolean)
    .join("\n\n");
  const merged = [existing?.trim(), extracted].filter(Boolean).join("\n\n");
  return { thinking: merged || null, answer };
}

const SUGGESTIONS = [
  "Implant Straumann giá bao nhiêu, có những loại nào?",
  "Quy trình tiệt trùng dụng cụ nha khoa theo WHO?",
  "5 thời điểm rửa tay (WHO 5 Moments) là gì?",
  "Bảng giá niềng răng mắc cài kim loại vs sứ?",
];

interface ThreadSummary {
  thread_id: string;
  title: string;
  message_count: number;
  updated_at: number;
  // Explicit, persisted server-side (agent/src/aiq_agent/memory/base.py) —
  // never inferred here from title or which list the item came from.
  mode: ChatMode;
  // Which agent this thread belongs to, or null/undefined for a plain
  // no-agent chat. Same set-once, never-inferred contract as `mode`.
  agent_id?: string | null;
}

interface HistoryMessage {
  role: string;
  content: string;
}

function makeThreadId(): string {
  return `t-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

export function ChatRoom({ cookieAgentId = null }: { cookieAgentId?: string | null } = {}) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [pending, setPending] = useState(false);
  // Separate active-thread pointer per mode — never a single shared
  // threadId, so switching modes can never accidentally continue writing
  // into the other mode's thread.
  const [chatThreadId, setChatThreadId] = useState<string | null>(null);
  const [agenticThreadId, setAgenticThreadId] = useState<string | null>(null);
  const [threads, setThreads] = useState<ThreadSummary[]>([]);
  const [loadingThread, setLoadingThread] = useState(false);
  const [chatInterim, setChatInterim] = useState("");
  const [mode, setMode] = useState<ChatMode>("chat");
  const [autoSubmitAfterMic, setAutoSubmitAfterMic] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const searchParams = useSearchParams();
  // When the URL includes ?agent=..., chat requests pull RAG context and
  // (for a manifest-driven agent) the entire system prompt from that
  // specific agent — never a different/default one. Users assigned an
  // agent land here via /my-agents, or via SideNav's agent switcher (which
  // navigates to /chat?agent=... precisely so this URL param and the
  // "active agent" cookie can never disagree — see side-nav.tsx
  // switchAgent). The cookie (passed down as `cookieAgentId` by the /chat
  // Server Component, which re-reads it on every router.refresh()) is only
  // a fallback for when the URL carries none at all — e.g. a super_admin/
  // tenant_owner landing on plain /chat. The URL always wins when present.
  // This is independent of `mode` on purpose — switching Chat ↔ Agentic
  // must never change the selected agent.
  const activeAgentId = searchParams.get("agent") || cookieAgentId || null;
  const requestedThreadParam = searchParams.get("thread");
  // Latest transcribed text — used to auto-submit after listening ends
  // without racing React's async state batching (setInput → onListeningChange).
  const pendingAutoSubmitRef = useRef<string | null>(null);

  const detectedUrls = findUrls(input);
  const threadId = mode === "agentic" ? agenticThreadId : chatThreadId;

  const refreshThreads = useCallback(async (forMode: ChatMode, forAgentId: string | null) => {
    try {
      const params = new URLSearchParams({ mode: forMode });
      if (forAgentId) params.set("agent_id", forAgentId);
      const res = await fetch(`/api/chat/threads?${params.toString()}`);
      if (!res.ok) return;
      const body = (await res.json()) as { items: ThreadSummary[] };
      // Server already filters by ?mode=/?agent_id= — this is a defensive
      // second guard so a stale/misbehaving response can never mix modes or
      // agents in the sidebar (requirement: legacy/other-agent threads must
      // never silently attach to the currently selected agent).
      const byMode = filterThreadsByMode(body.items ?? [], forMode);
      setThreads(filterThreadsByAgent(byMode, forAgentId));
    } catch {
      // silent
    }
  }, []);

  const loadThread = useCallback(
    async (tid: string): Promise<{ mode: ChatMode; agent_id: string | null } | null> => {
      setLoadingThread(true);
      try {
        const qs = activeAgentId ? `?agent_id=${encodeURIComponent(activeAgentId)}` : "";
        const res = await fetch(`/api/chat/threads/${encodeURIComponent(tid)}${qs}`);
        if (!res.ok) {
          setMessages([]);
          return null;
        }
        const body = (await res.json()) as {
          messages: HistoryMessage[];
          mode?: string;
          agent_id?: string | null;
        };
        const restored: Message[] = (body.messages ?? []).map((m, i) => {
          const isAssistant = m.role === "assistant";
          const { thinking, answer } = isAssistant
            ? extractThinking(m.content)
            : { thinking: null, answer: m.content };
          return {
            id: `h-${i}-${tid}`,
            role: isAssistant ? "assistant" : "user",
            content: answer,
            thinking,
          };
        });
        setMessages(restored);
        return { mode: normalizeMode(body.mode), agent_id: body.agent_id ?? null };
      } catch {
        setMessages([]);
        return null;
      } finally {
        setLoadingThread(false);
      }
    },
    [activeAgentId],
  );

  // Mic auto-submit is a global UI preference, not agent-scoped — read once.
  useEffect(() => {
    const storedAuto = window.localStorage.getItem(AUTO_SUBMIT_KEY);
    if (storedAuto === "1") setAutoSubmitAfterMic(true);
  }, []);

  // Re-runs whenever the resolved agent changes (URL ?agent= navigation, or
  // the cookie-derived fallback changing after a SideNav switch + Server
  // Component refresh) — requirement: switching agents must resume THAT
  // agent's own thread ids, never a previous agent's in-progress thread
  // (whose prior_history would otherwise bleed into the new agent's
  // context), and never silently reattach a thread that has no stored
  // agent_id to whichever agent is now selected.
  useEffect(() => {
    let cancelled = false;

    async function init() {
      const chatKey = storageKeyForAgentAndMode(activeAgentId, "chat");
      const agenticKey = storageKeyForAgentAndMode(activeAgentId, "agentic");

      const storedChat = window.localStorage.getItem(chatKey);
      const chatTid = storedChat ?? makeThreadId();
      if (!storedChat) window.localStorage.setItem(chatKey, chatTid);

      const storedAgentic = window.localStorage.getItem(agenticKey);
      const agenticTid = storedAgentic ?? makeThreadId();
      if (!storedAgentic) window.localStorage.setItem(agenticKey, agenticTid);

      if (cancelled) return;
      setChatThreadId(chatTid);
      setAgenticThreadId(agenticTid);
      setMode("chat");
      setMessages([]);
      setInput("");
      void refreshThreads("chat", activeAgentId);

      // Deep-link from SideNav's "Chat gần đây" (/chat?agent=X&thread=Y) —
      // only honored when the thread actually belongs to this agent;
      // otherwise silently fall back to the default thread rather than
      // showing another agent's history under the current selection.
      if (requestedThreadParam) {
        const meta = await loadThread(requestedThreadParam);
        if (cancelled) return;
        if (meta && threadBelongsToAgent({ agent_id: meta.agent_id }, activeAgentId)) {
          window.localStorage.setItem(
            storageKeyForAgentAndMode(activeAgentId, meta.mode),
            requestedThreadParam,
          );
          if (meta.mode === "agentic") setAgenticThreadId(requestedThreadParam);
          else setChatThreadId(requestedThreadParam);
          setMode(meta.mode);
          void refreshThreads(meta.mode, activeAgentId);
          return;
        }
        setMessages([]);
      }

      if (storedChat) {
        void loadThread(storedChat);
      }
    }

    void init();
    return () => {
      cancelled = true;
    };
  }, [activeAgentId, requestedThreadParam, refreshThreads, loadThread]);

  const toggleAutoSubmit = useCallback(() => {
    setAutoSubmitAfterMic((prev) => {
      const next = !prev;
      window.localStorage.setItem(AUTO_SUBMIT_KEY, next ? "1" : "0");
      return next;
    });
  }, []);

  useEffect(() => {
    scrollRef.current?.scrollTo({
      top: scrollRef.current.scrollHeight,
      behavior: "smooth",
    });
  }, [messages, pending]);

  const sendPrompt = useCallback(
    async (prompt: string) => {
      if (!prompt || pending) return;

      const userMsg: Message = {
        id: `u-${Date.now()}`,
        role: "user",
        content: prompt,
      };
      setMessages((m) => [...m, userMsg]);
      setInput("");
      setPending(true);

      try {
        const res = await fetch("/api/chat", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            prompt,
            thread_id: threadId ?? undefined,
            agent_id: activeAgentId ?? undefined,
          }),
        });

        if (!res.ok) {
          const errText = await res.text();
          let message = errText || "request failed";
          const looksLikeHtml = /^\s*<(!doctype|html|head|body)/i.test(errText);
          if (looksLikeHtml) {
            // Cloudflare/nginx error pages — never dump raw HTML into the chat.
            message =
              res.status === 504
                ? "Câu hỏi quá phức tạp, hệ thống chưa kịp trả lời trong giới hạn thời gian. Hãy chia nhỏ câu hoặc thử lại sau ít giây."
                : "Máy chủ tạm thời không phản hồi (proxy trả về trang lỗi). Vui lòng thử lại.";
          } else {
            try {
              const parsed = JSON.parse(errText) as {
                detail?: unknown;
                error?: string;
              };
              if (typeof parsed.error === "string") {
                message = parsed.error;
              } else if (Array.isArray(parsed.detail) && parsed.detail.length > 0) {
                const first = parsed.detail[0] as { msg?: string };
                message = first.msg ?? message;
              } else if (typeof parsed.detail === "string") {
                message = parsed.detail;
              }
            } catch {
              // fall back to raw text
            }
          }
          setMessages((m) => [
            ...m,
            {
              id: `e-${Date.now()}`,
              role: "system",
              content: `Lỗi ${res.status}: ${message}`,
            },
          ]);
          return;
        }

        const body = (await res.json()) as ChatApiResponse;
        const { thinking, answer } = extractThinking(body.final_answer, body.thinking);
        setMessages((m) => [
          ...m,
          {
            id: `a-${Date.now()}`,
            role: "assistant",
            content: answer,
            thinking,
            question: prompt,
            meta: { provider: body.provider, model: body.model },
          },
        ]);
        void refreshThreads("chat", activeAgentId);
      } catch (err) {
        const message = err instanceof Error ? err.message : "Network error";
        setMessages((m) => [
          ...m,
          { id: `e-${Date.now()}`, role: "system", content: message },
        ]);
      } finally {
        setPending(false);
      }
    },
    [pending, threadId, refreshThreads, activeAgentId],
  );

  const sendAgenticTask = useCallback(
    async (task: string) => {
      if (!task || pending) return;

      const userMsg: Message = {
        id: `u-${Date.now()}`,
        role: "user",
        content: task,
      };
      setMessages((m) => [...m, userMsg]);
      setInput("");
      setPending(true);

      try {
        const res = await fetch("/api/agent/run", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            task,
            max_turns: 8,
            agent_id: activeAgentId ?? undefined,
            thread_id: agenticThreadId ?? undefined,
          }),
        });

        if (!res.ok) {
          const errText = await res.text();
          let message = errText || "agentic request failed";
          const looksLikeHtml = /^\s*<(!doctype|html|head|body)/i.test(errText);
          if (looksLikeHtml) {
            message =
              res.status === 504
                ? "Tác vụ agentic quá dài, hệ thống chưa kịp trả lời. Hãy chia nhỏ task hoặc thử lại."
                : "Máy chủ tạm thời không phản hồi (proxy trả về trang lỗi). Vui lòng thử lại.";
          } else {
            try {
              const parsed = JSON.parse(errText) as {
                detail?: unknown;
                error?: string;
              };
              if (typeof parsed.error === "string") message = parsed.error;
              else if (typeof parsed.detail === "string") message = parsed.detail;
            } catch {
              // raw text
            }
          }
          setMessages((m) => [
            ...m,
            {
              id: `e-${Date.now()}`,
              role: "system",
              content: `Agent lỗi ${res.status}: ${message}`,
            },
          ]);
          return;
        }

        const body = (await res.json()) as AgentRunApiResponse;
        setMessages((m) => [
          ...m,
          {
            id: `a-${Date.now()}`,
            role: "assistant",
            content: body.final_answer,
            question: task,
            agenticSteps: body.steps ?? [],
          },
        ]);
        void refreshThreads("agentic", activeAgentId);
      } catch (err) {
        const message = err instanceof Error ? err.message : "Network error";
        setMessages((m) => [
          ...m,
          { id: `e-${Date.now()}`, role: "system", content: message },
        ]);
      } finally {
        setPending(false);
      }
    },
    [pending, activeAgentId, agenticThreadId, refreshThreads],
  );

  const send = useCallback(() => {
    const text = input.trim();
    if (!text) return;
    if (mode === "agentic") {
      void sendAgenticTask(text);
    } else {
      void sendPrompt(text);
    }
  }, [input, mode, sendAgenticTask, sendPrompt]);

  // Switching the toggle must switch history immediately, never mix Chat
  // and Agentic, and never touch the selected agent (activeAgentId is
  // derived from the URL, untouched here). Each mode remembers its own
  // active thread — reselecting a mode restores whatever thread it had.
  const changeMode = useCallback(
    (next: ChatMode) => {
      if (next === mode) return;
      setMode(next);
      setMessages([]);
      setInput("");
      void refreshThreads(next, activeAgentId);
      const tid = next === "agentic" ? agenticThreadId : chatThreadId;
      if (tid) void loadThread(tid);
    },
    [mode, agenticThreadId, chatThreadId, refreshThreads, loadThread, activeAgentId],
  );

  // "+ Mới" always creates a thread in the CURRENTLY selected mode AND the
  // currently selected agent — never touches another (mode, agent) bucket.
  function newThread() {
    const fresh = makeThreadId();
    window.localStorage.setItem(storageKeyForAgentAndMode(activeAgentId, mode), fresh);
    if (mode === "agentic") setAgenticThreadId(fresh);
    else setChatThreadId(fresh);
    setMessages([]);
    setInput("");
  }

  // Called with the full ThreadSummary (not just an id) so a click always
  // restores the item's OWN persisted mode, even defensively — never
  // assumes "whatever list it came from" implies the mode. Also refuses to
  // open a thread belonging to a different agent — the sidebar list is
  // already agent-filtered so this should never trigger in practice, but it
  // is the requirement's explicit backstop against cross-agent bleed.
  function switchThread(item: ThreadSummary) {
    if (!threadBelongsToAgent(item, activeAgentId)) return;
    const restoredMode = modeForHistoryItem(item);
    if (item.thread_id === threadId && restoredMode === mode) return;
    window.localStorage.setItem(storageKeyForAgentAndMode(activeAgentId, restoredMode), item.thread_id);
    if (restoredMode === "agentic") setAgenticThreadId(item.thread_id);
    else setChatThreadId(item.thread_id);
    if (restoredMode !== mode) setMode(restoredMode);
    setMessages([]);
    setInput("");
    void loadThread(item.thread_id);
  }

  async function deleteThread(tid: string) {
    try {
      await fetch(`/api/chat/threads/${encodeURIComponent(tid)}`, { method: "DELETE" });
    } catch {
      // silent
    }
    if (tid === threadId) {
      newThread();
    }
    void refreshThreads(mode, activeAgentId);
  }

  return (
    <div className="grid h-[calc(100dvh-120px)] gap-4 lg:grid-cols-[280px_1fr]">
      <ThreadSidebar
        mode={mode}
        threads={threads}
        activeId={threadId}
        onNew={newThread}
        onSelect={switchThread}
        onDelete={(id) => void deleteThread(id)}
      />

      <div className="flex min-h-0 flex-col">
        <header className="flex items-center justify-between gap-3 pb-3">
          <div className="min-w-0">
            <h1 className="font-serif text-2xl">Chat</h1>
            <p className="truncate text-xs text-[var(--color-muted-fg)]">
              {messages.length > 0 && messages[0].role === "user"
                ? messages[0].content.slice(0, 80)
                : "Cuộc trò chuyện mới"}
            </p>
          </div>
          <div className="flex items-center gap-2">
            <ModeToggle mode={mode} onChange={changeMode} />
            <Button variant="ghost" size="sm" onClick={newThread}>
              <Plus className="size-4" /> New
            </Button>
          </div>
        </header>

        <div
          ref={scrollRef}
          className="min-h-0 flex-1 space-y-6 overflow-y-auto rounded-[var(--radius)] border bg-[var(--color-bg)] p-6"
        >
          {loadingThread ? (
            <div className="flex items-center justify-center gap-2 py-10 text-sm text-[var(--color-muted-fg)]">
              <Loader2 className="size-4 animate-spin" /> Đang tải lịch sử…
            </div>
          ) : (
            <>
              {messages.length === 0 && (
                <EmptyState onPick={(s) => void sendPrompt(s)} />
              )}
              {messages.map((m) => (
                <MessageBubble key={m.id} message={m} />
              ))}
              {pending && <TypingIndicator />}
            </>
          )}
        </div>

        <div className="mt-3 space-y-2">
          {chatInterim && (
            <div className="rounded-[var(--radius)] border border-dashed border-[var(--color-primary)]/40 bg-[var(--color-primary)]/5 px-4 py-2 text-sm italic text-[var(--color-muted-fg)]">
              <span className="mr-2 text-[10px] uppercase tracking-wide text-[var(--color-primary)]">
                Đang nghe
              </span>
              {chatInterim}…
            </div>
          )}
          {mode === "chat" && detectedUrls.length > 0 && !pending && (
            <div className="flex flex-wrap items-center gap-2 rounded-[var(--radius)] border border-dashed border-[var(--color-accent)]/50 bg-[var(--color-accent)]/10 px-3 py-2 text-xs text-[var(--color-fg)]">
              <LinkIcon className="size-3.5 text-[var(--color-accent)]" />
              <span>
                Phát hiện URL — chuyển sang <strong>Agentic</strong> để tự động
                chuyển thành note?
              </span>
              <button
                type="button"
                onClick={() => {
                  setMode("agentic");
                  setInput(
                    `Lấy nội dung từ URL ${detectedUrls[0]} rồi lưu thành 1 note trong kiến thức.`,
                  );
                }}
                className="ml-auto rounded-full bg-[var(--color-accent)] px-3 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[var(--color-fg)] hover:opacity-90"
              >
                Chuyển sang Agentic
              </button>
            </div>
          )}
          <form
            className="flex items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              send();
            }}
          >
            <div className="relative flex-1">
              <input
                value={input}
                onChange={(e) => setInput(e.target.value)}
                placeholder={
                  mode === "agentic"
                    ? "Mô tả task (vd: tổng hợp các Note về Implant thành cheatsheet)…"
                    : "Hỏi gì đó về kiến thức của bạn…"
                }
                className={`h-12 w-full rounded-[var(--radius)] border bg-[var(--color-bg)] px-4 pr-12 text-sm focus:outline-none ${
                  mode === "agentic"
                    ? "border-[var(--color-accent)] focus:border-[var(--color-accent)]"
                    : "border-[var(--color-border)] focus:border-[var(--color-primary)]"
                }`}
                disabled={pending}
              />
              <div className="absolute right-2 top-1/2 -translate-y-1/2 flex items-center gap-1">
                <button
                  type="button"
                  onClick={toggleAutoSubmit}
                  title={
                    autoSubmitAfterMic
                      ? "Đang bật: nói xong tự động gửi. Bấm để tắt."
                      : "Đang tắt: chỉ điền vào ô nhập, phải bấm Send. Bấm để bật auto-gửi."
                  }
                  aria-pressed={autoSubmitAfterMic}
                  className={`inline-flex size-7 items-center justify-center rounded-full border transition ${
                    autoSubmitAfterMic
                      ? "border-emerald-500 bg-emerald-500 text-white"
                      : "border-[var(--color-border)] bg-[var(--color-bg)] text-[var(--color-muted-fg)] hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
                  }`}
                >
                  {autoSubmitAfterMic ? (
                    <Zap className="size-3.5" />
                  ) : (
                    <ZapOff className="size-3.5" />
                  )}
                </button>
                <MicButton
                  onTranscript={(text) => {
                    setInput((prev) => {
                      const next = prev ? `${prev.trimEnd()} ${text}` : text;
                      pendingAutoSubmitRef.current = next;
                      return next;
                    });
                  }}
                  onInterim={setChatInterim}
                  onListeningChange={(listening) => {
                    // When user stops recording AND auto-submit is on,
                    // fire the send immediately with the buffered text.
                    // sendPrompt already clears the input on its own.
                    if (!listening && autoSubmitAfterMic) {
                      const buffered = pendingAutoSubmitRef.current;
                      pendingAutoSubmitRef.current = null;
                      const trimmed = buffered?.trim();
                      if (trimmed) void sendPrompt(trimmed);
                    }
                  }}
                  disabled={pending}
                  size="sm"
                />
              </div>
            </div>
            <Button type="submit" size="lg" disabled={pending || !input.trim()}>
              {mode === "agentic" ? <Wand2 className="size-4" /> : <Send className="size-4" />}
              {mode === "agentic" ? "Run" : "Send"}
            </Button>
          </form>
          <p className="mt-1.5 text-[10px] text-[var(--color-muted-fg)]">
            {autoSubmitAfterMic ? (
              <>
                <Zap className="mr-1 inline size-3 text-emerald-600" />
                Auto-gửi bật — bấm mic, nói, bấm mic lần nữa để gửi ngay.
              </>
            ) : (
              <>
                Mẹo: bấm biểu tượng <ZapOff className="inline size-3" /> bên trái mic để bật <strong>Auto-gửi</strong> — nói xong bấm mic dừng là gửi luôn, không cần bấm Send.
              </>
            )}
          </p>
        </div>
      </div>
    </div>
  );
}

function ThreadSidebar({
  mode,
  threads,
  activeId,
  onNew,
  onSelect,
  onDelete,
}: {
  mode: ChatMode;
  threads: ThreadSummary[];
  activeId: string | null;
  onNew: () => void;
  onSelect: (item: ThreadSummary) => void;
  onDelete: (id: string) => void;
}) {
  const title = mode === "agentic" ? "LỊCH SỬ AGENT" : "LỊCH SỬ CHAT";
  const emptyLabel =
    mode === "agentic"
      ? "Chưa có tác vụ agentic nào. Mô tả 1 task để bắt đầu."
      : "Chưa có cuộc trò chuyện nào. Hỏi câu đầu tiên để bắt đầu.";
  return (
    <aside className="hidden min-h-0 flex-col rounded-[var(--radius)] border bg-[var(--color-bg)] lg:flex">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wide text-[var(--color-muted-fg)]">
          {title}
        </span>
        <Button size="sm" variant="ghost" onClick={onNew}>
          <Plus className="size-3.5" /> Mới
        </Button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
        {threads.length === 0 ? (
          <p className="px-2 py-3 text-xs text-[var(--color-muted-fg)]">{emptyLabel}</p>
        ) : (
          <ul className="space-y-1">
            {threads.map((t) => {
              const isActive = t.thread_id === activeId;
              return (
                <li key={t.thread_id}>
                  <div
                    className={`group flex items-center gap-1 rounded-[var(--radius)] px-2 py-1.5 text-sm transition ${
                      isActive
                        ? "bg-[var(--color-primary)]/10 text-[var(--color-fg)]"
                        : "text-[var(--color-fg)] hover:bg-[var(--color-muted)]"
                    }`}
                  >
                    <button
                      type="button"
                      onClick={() => onSelect(t)}
                      className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    >
                      {t.mode === "agentic" ? (
                        <Wand2 className="size-3.5 shrink-0 text-[var(--color-accent)]" />
                      ) : (
                        <MessageSquare className="size-3.5 shrink-0 text-[var(--color-muted-fg)]" />
                      )}
                      <span className="truncate text-xs">{t.title || "Cuộc trò chuyện"}</span>
                    </button>
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        if (window.confirm("Xoá cuộc trò chuyện này?")) {
                          onDelete(t.thread_id);
                        }
                      }}
                      className="shrink-0 rounded p-1 text-[var(--color-muted-fg)] opacity-0 transition group-hover:opacity-100 hover:bg-red-50 hover:text-red-600"
                      aria-label="Delete thread"
                    >
                      <Trash2 className="size-3" />
                    </button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </aside>
  );
}

function EmptyState({ onPick }: { onPick: (s: string) => void }) {
  return (
    <div className="flex flex-col items-center justify-center gap-6 py-10 text-center">
      <div className="flex size-12 items-center justify-center rounded-full bg-[var(--color-primary)]/10 text-[var(--color-primary)]">
        <Sparkles className="size-6" />
      </div>
      <div>
        <h2 className="font-serif text-xl">Bắt đầu với AIQ</h2>
        <p className="mt-1 max-w-md text-sm text-[var(--color-muted-fg)]">
          Trợ lý đã được nạp kiến thức bạn lưu trong Notes. Hỏi bất kỳ chủ đề
          nào — câu trả lời sẽ trích dẫn Note tham chiếu.
        </p>
      </div>
      <div className="grid w-full max-w-2xl grid-cols-1 gap-2 sm:grid-cols-2">
        {SUGGESTIONS.map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => onPick(s)}
            className="rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-muted)] px-4 py-3 text-left text-sm text-[var(--color-fg)] transition hover:border-[var(--color-primary)]"
          >
            {s}
          </button>
        ))}
      </div>
    </div>
  );
}

function TypingIndicator() {
  return (
    <div className="flex items-center gap-3">
      <div className="flex size-8 items-center justify-center rounded-full bg-[var(--color-muted)] text-[var(--color-muted-fg)]">
        <Sparkles className="size-4" />
      </div>
      <div className="flex gap-1">
        <span className="size-2 animate-bounce rounded-full bg-[var(--color-muted-fg)] [animation-delay:-0.3s]" />
        <span className="size-2 animate-bounce rounded-full bg-[var(--color-muted-fg)] [animation-delay:-0.15s]" />
        <span className="size-2 animate-bounce rounded-full bg-[var(--color-muted-fg)]" />
      </div>
    </div>
  );
}

function MessageBubble({ message }: { message: Message }) {
  if (message.role === "system") {
    return (
      <div className="mx-auto max-w-[90%] rounded-[var(--radius)] border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-700">
        {message.content}
      </div>
    );
  }

  if (message.role === "user") {
    return (
      <div className="flex items-start justify-end gap-3">
        <div className="max-w-[80%] rounded-[var(--radius)] bg-[var(--color-primary)] px-4 py-2.5 text-sm text-[var(--color-primary-fg)]">
          <p className="whitespace-pre-wrap leading-relaxed">{message.content}</p>
        </div>
        <div className="flex size-8 shrink-0 items-center justify-center rounded-full bg-[var(--color-primary)] text-[var(--color-primary-fg)]">
          <UserIcon className="size-4" />
        </div>
      </div>
    );
  }

  const isAgentic = (message.agenticSteps?.length ?? 0) > 0;

  return (
    <div className="flex items-start gap-3">
      <div
        className={`flex size-8 shrink-0 items-center justify-center rounded-full ${
          isAgentic
            ? "bg-[var(--color-accent)]/20 text-[var(--color-accent)]"
            : "bg-[var(--color-muted)] text-[var(--color-muted-fg)]"
        }`}
      >
        {isAgentic ? <Wand2 className="size-4" /> : <Sparkles className="size-4" />}
      </div>
      <div className="min-w-0 max-w-[85%] flex-1 space-y-2">
        {message.thinking && <ThinkingBlock text={message.thinking} />}
        {isAgentic && message.agenticSteps && message.agenticSteps.length > 1 && (
          <AgenticTrace steps={message.agenticSteps} />
        )}
        <div
          className={`rounded-[var(--radius)] border px-4 py-3 text-sm text-[var(--color-fg)] ${
            isAgentic
              ? "border-[var(--color-accent)]/40 bg-[var(--color-accent)]/5"
              : "bg-[var(--color-muted)]"
          }`}
        >
          <MarkdownContent text={message.content} />
        </div>
        <div className="flex items-center justify-between gap-2 px-1">
          {message.meta?.model ? (
            <p className="text-[10px] text-[var(--color-muted-fg)]">
              {message.meta.provider} · {message.meta.model}
            </p>
          ) : isAgentic ? (
            <p className="text-[10px] uppercase tracking-wide text-[var(--color-accent)]">
              Agentic · {message.agenticSteps?.length ?? 0} bước
            </p>
          ) : (
            <span />
          )}
          <SaveToKbButton
            defaultTitle={message.question ?? "Câu trả lời từ chat"}
            defaultContent={buildSaveContent(message.question, message.content)}
            variant="chip"
          />
        </div>
      </div>
    </div>
  );
}

function AgenticTrace({ steps }: { steps: AgenticStep[] }) {
  const [open, setOpen] = useState(false);
  return (
    <details
      className="rounded-[var(--radius)] border border-dashed border-[var(--color-accent)]/50 bg-[var(--color-bg)] text-xs text-[var(--color-muted-fg)]"
      onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}
    >
      <summary className="flex cursor-pointer select-none items-center gap-2 px-3 py-2 font-medium text-[var(--color-fg)]">
        <Bot className="size-3.5 text-[var(--color-accent)]" />
        Trace agentic · {steps.length} bước {open ? "" : "(nhấn để xem)"}
      </summary>
      <ol className="border-t border-dashed border-[var(--color-accent)]/40 px-3 py-2 leading-relaxed">
        {steps.map((step, idx) => (
          <li key={`${idx}-${step.text.slice(0, 24)}`} className="mb-1.5 last:mb-0">
            <span className="mr-2 inline-block min-w-5 text-[var(--color-accent)]">
              {idx + 1}.
            </span>
            <span className="whitespace-pre-wrap">{step.text}</span>
          </li>
        ))}
      </ol>
    </details>
  );
}

function ModeToggle({
  mode,
  onChange,
}: {
  mode: ChatMode;
  onChange: (mode: ChatMode) => void;
}) {
  return (
    <div className="flex items-center rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] p-0.5 text-xs">
      <button
        type="button"
        onClick={() => onChange("chat")}
        className={`flex items-center gap-1 rounded-full px-3 py-1 transition ${
          mode === "chat"
            ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
            : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        }`}
        title="Chat thường — RAG + skills"
      >
        <Sparkles className="size-3" />
        Chat
      </button>
      <button
        type="button"
        onClick={() => onChange("agentic")}
        className={`flex items-center gap-1 rounded-full px-3 py-1 transition ${
          mode === "agentic"
            ? "bg-[var(--color-accent)] text-[var(--color-fg)]"
            : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        }`}
        title="Agentic — multi-step + tools (search_notes, fetch_url, save_note)"
      >
        <Wand2 className="size-3" />
        Agentic
      </button>
    </div>
  );
}

function buildSaveContent(question: string | undefined, answer: string): string {
  if (!question) return answer;
  return `**Câu hỏi:** ${question}\n\n${answer}`;
}

function ThinkingBlock({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <details
      className="rounded-[var(--radius)] border border-dashed border-[var(--color-border)] bg-[var(--color-bg)] text-xs text-[var(--color-muted-fg)]"
      onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}
    >
      <summary className="flex cursor-pointer select-none items-center gap-2 px-3 py-2 font-medium text-[var(--color-fg)]">
        <Brain className="size-3.5" />
        Suy nghĩ {open ? "" : "(nhấn để xem)"}
      </summary>
      <div className="border-t border-dashed border-[var(--color-border)] px-3 py-2 leading-relaxed">
        <p className="whitespace-pre-wrap">{text}</p>
      </div>
    </details>
  );
}

function MarkdownContent({ text }: { text: string }) {
  return (
    <div className="prose prose-sm max-w-none text-[var(--color-fg)] [&_a]:text-[var(--color-primary)] [&_a]:underline [&_blockquote]:border-l-2 [&_blockquote]:border-[var(--color-border)] [&_blockquote]:pl-3 [&_blockquote]:text-[var(--color-muted-fg)] [&_h1]:mb-2 [&_h1]:mt-3 [&_h1]:font-serif [&_h1]:text-lg [&_h2]:mb-2 [&_h2]:mt-3 [&_h2]:font-serif [&_h2]:text-base [&_h3]:mb-1.5 [&_h3]:mt-2.5 [&_h3]:font-semibold [&_h3]:text-sm [&_hr]:my-3 [&_hr]:border-[var(--color-border)] [&_li]:my-0.5 [&_ol]:my-2 [&_ol]:list-decimal [&_ol]:pl-5 [&_p]:my-2 [&_p]:leading-relaxed [&_strong]:font-semibold [&_table]:my-2 [&_table]:w-full [&_table]:border-collapse [&_table]:text-xs [&_td]:border [&_td]:border-[var(--color-border)] [&_td]:px-2 [&_td]:py-1 [&_th]:border [&_th]:border-[var(--color-border)] [&_th]:bg-[var(--color-bg)] [&_th]:px-2 [&_th]:py-1 [&_th]:text-left [&_th]:font-semibold [&_ul]:my-2 [&_ul]:list-disc [&_ul]:pl-5">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          code(props) {
            const { className, children, ...rest } = props;
            const match = /language-(\w+)/.exec(className ?? "");
            const isBlock = Boolean(match) || String(children).includes("\n");
            if (!isBlock) {
              return (
                <code
                  className="rounded bg-[var(--color-bg)] px-1.5 py-0.5 font-mono text-[0.85em] text-[var(--color-primary)]"
                  {...rest}
                >
                  {children}
                </code>
              );
            }
            const lang = match?.[1] ?? "code";
            const raw = String(children).replace(/\n$/, "");
            return <CodeBlock lang={lang} code={raw} />;
          },
          pre({ children }) {
            return <>{children}</>;
          },
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}

function CodeBlock({ lang, code }: { lang: string; code: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard unavailable
    }
  }

  return (
    <div className="my-3 overflow-hidden rounded-[var(--radius)] border border-zinc-800 bg-zinc-950 text-zinc-100">
      <div className="flex items-center justify-between border-b border-zinc-800 px-3 py-1.5">
        <span className="font-mono text-[10px] uppercase tracking-wide text-zinc-400">
          {lang}
        </span>
        <button
          type="button"
          onClick={copy}
          className="flex items-center gap-1 rounded px-2 py-0.5 text-[10px] text-zinc-300 hover:bg-zinc-800"
        >
          {copied ? (
            <>
              <Check className="size-3" /> Copied
            </>
          ) : (
            <>
              <Copy className="size-3" /> Copy
            </>
          )}
        </button>
      </div>
      <pre className="overflow-x-auto px-4 py-3 text-xs leading-relaxed">
        <code className="font-mono">{code}</code>
      </pre>
    </div>
  );
}
