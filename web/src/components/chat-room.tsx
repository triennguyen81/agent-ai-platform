"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  BookmarkPlus,
  Brain,
  Check,
  Copy,
  Loader2,
  MessageSquare,
  Plus,
  Send,
  Sparkles,
  Trash2,
  User as UserIcon,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";
import { MicButton } from "@/components/mic-button";

interface Message {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  thinking?: string | null;
  question?: string;
  meta?: { provider?: string; model?: string };
}

interface ChatApiResponse {
  final_answer: string;
  provider: string;
  model: string;
  thread_id?: string | null;
  thinking?: string | null;
  steps?: unknown[];
}

const THREAD_KEY = "aiq.chat.thread_id";

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
}

interface HistoryMessage {
  role: string;
  content: string;
}

function makeThreadId(): string {
  return `t-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

export function ChatRoom() {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [pending, setPending] = useState(false);
  const [threadId, setThreadId] = useState<string | null>(null);
  const [threads, setThreads] = useState<ThreadSummary[]>([]);
  const [loadingThread, setLoadingThread] = useState(false);
  const [chatInterim, setChatInterim] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);

  const refreshThreads = useCallback(async () => {
    try {
      const res = await fetch("/api/chat/threads");
      if (!res.ok) return;
      const body = (await res.json()) as { items: ThreadSummary[] };
      setThreads(body.items ?? []);
    } catch {
      // silent
    }
  }, []);

  const loadThread = useCallback(async (tid: string) => {
    setLoadingThread(true);
    try {
      const res = await fetch(`/api/chat/threads/${encodeURIComponent(tid)}`);
      if (!res.ok) {
        setMessages([]);
        return;
      }
      const body = (await res.json()) as { messages: HistoryMessage[] };
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
    } catch {
      setMessages([]);
    } finally {
      setLoadingThread(false);
    }
  }, []);

  useEffect(() => {
    const stored = window.localStorage.getItem(THREAD_KEY);
    const tid = stored ?? makeThreadId();
    if (!stored) {
      window.localStorage.setItem(THREAD_KEY, tid);
    }
    setThreadId(tid);
    void refreshThreads();
    if (stored) {
      void loadThread(stored);
    }
  }, [refreshThreads, loadThread]);

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
          body: JSON.stringify({ prompt, thread_id: threadId ?? undefined }),
        });

        if (!res.ok) {
          const errText = await res.text();
          let message = errText || "request failed";
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
          setMessages((m) => [
            ...m,
            {
              id: `e-${Date.now()}`,
              role: "system",
              content: `Error ${res.status}: ${message}`,
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
        void refreshThreads();
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
    [pending, threadId, refreshThreads],
  );

  const send = useCallback(() => {
    void sendPrompt(input.trim());
  }, [input, sendPrompt]);

  function newThread() {
    const fresh = makeThreadId();
    window.localStorage.setItem(THREAD_KEY, fresh);
    setThreadId(fresh);
    setMessages([]);
    setInput("");
  }

  function switchThread(tid: string) {
    if (tid === threadId) return;
    window.localStorage.setItem(THREAD_KEY, tid);
    setThreadId(tid);
    setMessages([]);
    setInput("");
    void loadThread(tid);
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
    void refreshThreads();
  }

  return (
    <div className="grid h-[calc(100dvh-120px)] gap-4 lg:grid-cols-[280px_1fr]">
      <ThreadSidebar
        threads={threads}
        activeId={threadId}
        onNew={newThread}
        onSelect={switchThread}
        onDelete={(id) => void deleteThread(id)}
      />

      <div className="flex min-h-0 flex-col">
        <header className="flex items-center justify-between pb-3">
          <div className="min-w-0">
            <h1 className="font-serif text-2xl">Chat</h1>
            <p className="truncate text-xs text-[var(--color-muted-fg)]">
              {messages.length > 0 && messages[0].role === "user"
                ? messages[0].content.slice(0, 80)
                : "Cuộc trò chuyện mới"}
            </p>
          </div>
          <Button variant="ghost" size="sm" onClick={newThread}>
            <Plus className="size-4" /> New
          </Button>
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
                placeholder="Hỏi gì đó về kiến thức của bạn…"
                className="h-12 w-full rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-4 pr-12 text-sm focus:border-[var(--color-primary)] focus:outline-none"
                disabled={pending}
              />
              <div className="absolute right-2 top-1/2 -translate-y-1/2">
                <MicButton
                  onTranscript={(text) =>
                    setInput((prev) =>
                      prev ? `${prev.trimEnd()} ${text}` : text,
                    )
                  }
                  onInterim={setChatInterim}
                  disabled={pending}
                  size="sm"
                />
              </div>
            </div>
            <Button type="submit" size="lg" disabled={pending || !input.trim()}>
              <Send className="size-4" />
              Send
            </Button>
          </form>
        </div>
      </div>
    </div>
  );
}

function ThreadSidebar({
  threads,
  activeId,
  onNew,
  onSelect,
  onDelete,
}: {
  threads: ThreadSummary[];
  activeId: string | null;
  onNew: () => void;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
}) {
  return (
    <aside className="hidden min-h-0 flex-col rounded-[var(--radius)] border bg-[var(--color-bg)] lg:flex">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wide text-[var(--color-muted-fg)]">
          Lịch sử
        </span>
        <Button size="sm" variant="ghost" onClick={onNew}>
          <Plus className="size-3.5" /> Mới
        </Button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
        {threads.length === 0 ? (
          <p className="px-2 py-3 text-xs text-[var(--color-muted-fg)]">
            Chưa có cuộc trò chuyện nào. Hỏi câu đầu tiên để bắt đầu.
          </p>
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
                      onClick={() => onSelect(t.thread_id)}
                      className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    >
                      <MessageSquare className="size-3.5 shrink-0 text-[var(--color-muted-fg)]" />
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

  return (
    <div className="flex items-start gap-3">
      <div className="flex size-8 shrink-0 items-center justify-center rounded-full bg-[var(--color-muted)] text-[var(--color-muted-fg)]">
        <Sparkles className="size-4" />
      </div>
      <div className="min-w-0 max-w-[85%] flex-1 space-y-2">
        {message.thinking && <ThinkingBlock text={message.thinking} />}
        <div className="rounded-[var(--radius)] border bg-[var(--color-muted)] px-4 py-3 text-sm text-[var(--color-fg)]">
          <MarkdownContent text={message.content} />
        </div>
        <div className="flex items-center justify-between gap-2 px-1">
          {message.meta?.model ? (
            <p className="text-[10px] text-[var(--color-muted-fg)]">
              {message.meta.provider} · {message.meta.model}
            </p>
          ) : (
            <span />
          )}
          <SaveToKnowledgeButton
            defaultTitle={message.question ?? "Câu trả lời từ chat"}
            defaultContent={buildSaveContent(message.question, message.content)}
          />
        </div>
      </div>
    </div>
  );
}

function buildSaveContent(question: string | undefined, answer: string): string {
  if (!question) return answer;
  return `**Câu hỏi:** ${question}\n\n${answer}`;
}

function SaveToKnowledgeButton({
  defaultTitle,
  defaultContent,
}: {
  defaultTitle: string;
  defaultContent: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="flex items-center gap-1 rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-0.5 text-[10px] text-[var(--color-muted-fg)] hover:border-[var(--color-primary)] hover:text-[var(--color-primary)]"
      >
        <BookmarkPlus className="size-3" />
        Lưu vào kiến thức
      </button>
      {open && (
        <SaveModal
          defaultTitle={defaultTitle}
          defaultContent={defaultContent}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}

function SaveModal({
  defaultTitle,
  defaultContent,
  onClose,
}: {
  defaultTitle: string;
  defaultContent: string;
  onClose: () => void;
}) {
  const [title, setTitle] = useState(defaultTitle.slice(0, 300));
  const [content, setContent] = useState(defaultContent);
  const [tagsInput, setTagsInput] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [interim, setInterim] = useState("");

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (saving) return;
    setSaving(true);
    setError(null);

    const tags = tagsInput
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    try {
      const res = await fetch("/api/notes", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: title.trim(), content, tags }),
      });
      if (!res.ok) {
        const body = await res.text();
        setError(body.slice(0, 300) || `HTTP ${res.status}`);
        return;
      }
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <form
        onSubmit={submit}
        className="w-full max-w-lg space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5 shadow-xl"
      >
        <div className="flex items-start justify-between">
          <div>
            <h3 className="font-serif text-lg">Lưu vào kiến thức</h3>
            <p className="text-xs text-[var(--color-muted-fg)]">
              Câu trả lời này sẽ được dùng cho các chat sau (RAG).
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
            aria-label="Close"
          >
            <X className="size-4" />
          </button>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="kb-title">Tiêu đề</Label>
          <Input
            id="kb-title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            maxLength={300}
            required
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="kb-content">Nội dung (markdown ok)</Label>
          {interim && (
            <div className="rounded-[var(--radius)] border border-dashed border-[var(--color-primary)]/40 bg-[var(--color-primary)]/5 px-3 py-1.5 text-xs italic text-[var(--color-muted-fg)]">
              <span className="mr-2 text-[10px] uppercase tracking-wide text-[var(--color-primary)]">
                Đang nghe
              </span>
              {interim}…
            </div>
          )}
          <div className="relative">
            <Textarea
              id="kb-content"
              value={content}
              onChange={(e) => setContent(e.target.value)}
              rows={10}
            />
            <div className="absolute bottom-2 right-2">
              <MicButton
                onTranscript={(text) =>
                  setContent((prev) =>
                    prev ? `${prev.trimEnd()} ${text}` : text,
                  )
                }
                onInterim={setInterim}
                size="sm"
              />
            </div>
          </div>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="kb-tags">Tags (cách nhau bằng dấu phẩy)</Label>
          <Input
            id="kb-tags"
            value={tagsInput}
            onChange={(e) => setTagsInput(e.target.value)}
            placeholder="chat, dental, faq"
          />
        </div>
        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="ghost" size="sm" onClick={onClose}>
            Huỷ
          </Button>
          <Button type="submit" size="sm" disabled={saving || !title.trim()}>
            {saving ? (
              <>
                <Loader2 className="mr-1 size-3.5 animate-spin" />
                Đang lưu…
              </>
            ) : (
              "Lưu note"
            )}
          </Button>
        </div>
      </form>
    </div>
  );
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
