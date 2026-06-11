"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Brain, Check, Copy, Send, Sparkles, User as UserIcon } from "lucide-react";
import { Button } from "@/components/ui/button";

interface Message {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  thinking?: string | null;
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

export function ChatRoom() {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [pending, setPending] = useState(false);
  const [threadId, setThreadId] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const stored = window.localStorage.getItem(THREAD_KEY);
    if (stored) {
      setThreadId(stored);
    } else {
      const fresh = `t-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
      window.localStorage.setItem(THREAD_KEY, fresh);
      setThreadId(fresh);
    }
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
            meta: { provider: body.provider, model: body.model },
          },
        ]);
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
    [pending, threadId],
  );

  const send = useCallback(() => {
    void sendPrompt(input.trim());
  }, [input, sendPrompt]);

  function newThread() {
    const fresh = `t-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
    window.localStorage.setItem(THREAD_KEY, fresh);
    setThreadId(fresh);
    setMessages([]);
  }

  return (
    <div className="flex h-[calc(100dvh-120px)] flex-col">
      <header className="flex items-center justify-between pb-4">
        <div>
          <h1 className="font-serif text-2xl">Chat</h1>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Thread: {threadId ?? "…"}
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={newThread}>
          New thread
        </Button>
      </header>

      <div
        ref={scrollRef}
        className="flex-1 space-y-6 overflow-y-auto rounded-[var(--radius)] border bg-[var(--color-bg)] p-6"
      >
        {messages.length === 0 && (
          <EmptyState onPick={(s) => void sendPrompt(s)} />
        )}
        {messages.map((m) => (
          <MessageBubble key={m.id} message={m} />
        ))}
        {pending && <TypingIndicator />}
      </div>

      <form
        className="mt-4 flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          send();
        }}
      >
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Hỏi gì đó về kiến thức của bạn…"
          className="h-12 flex-1 rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-4 text-sm focus:border-[var(--color-primary)] focus:outline-none"
          disabled={pending}
        />
        <Button type="submit" size="lg" disabled={pending || !input.trim()}>
          <Send className="size-4" />
          Send
        </Button>
      </form>
    </div>
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
        {message.meta?.model && (
          <p className="px-1 text-[10px] text-[var(--color-muted-fg)]">
            {message.meta.provider} · {message.meta.model}
          </p>
        )}
      </div>
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
