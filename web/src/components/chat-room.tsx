"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";

type Message = {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  meta?: { provider?: string; model?: string };
};

type ChatResponse = {
  final_answer: string;
  provider: string;
  model: string;
  thread_id?: string | null;
  steps?: unknown[];
};

const THREAD_KEY = "aiq.chat.thread_id";

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
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages]);

  const send = useCallback(async () => {
    const prompt = input.trim();
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
        setMessages((m) => [
          ...m,
          {
            id: `e-${Date.now()}`,
            role: "system",
            content: `Error ${res.status}: ${errText || "request failed"}`,
          },
        ]);
        return;
      }

      const body = (await res.json()) as ChatResponse;
      setMessages((m) => [
        ...m,
        {
          id: `a-${Date.now()}`,
          role: "assistant",
          content: body.final_answer,
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
  }, [input, pending, threadId]);

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
        className="flex-1 space-y-4 overflow-y-auto rounded-[var(--radius)] border p-4"
      >
        {messages.length === 0 && (
          <p className="text-center text-sm text-[var(--color-muted-fg)]">
            Ask anything. Your conversation memory persists across turns.
          </p>
        )}
        {messages.map((m) => (
          <div
            key={m.id}
            className={
              m.role === "user"
                ? "ml-auto max-w-[80%] rounded-[var(--radius)] bg-[var(--color-primary)] px-4 py-2 text-sm text-[var(--color-primary-fg)]"
                : m.role === "system"
                ? "mx-auto max-w-[90%] rounded-[var(--radius)] bg-red-50 px-4 py-2 text-sm text-red-700"
                : "mr-auto max-w-[85%] rounded-[var(--radius)] border bg-[var(--color-muted)] px-4 py-2 text-sm text-[var(--color-fg)]"
            }
          >
            <p className="whitespace-pre-wrap">{m.content}</p>
            {m.meta?.model && (
              <p className="mt-1 text-[10px] opacity-60">
                {m.meta.provider} · {m.meta.model}
              </p>
            )}
          </div>
        ))}
        {pending && (
          <p className="text-sm text-[var(--color-muted-fg)]">…thinking</p>
        )}
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
          placeholder="Type a message…"
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
