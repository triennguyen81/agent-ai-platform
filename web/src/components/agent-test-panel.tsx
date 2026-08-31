"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";

interface TestResult {
  final_answer: string;
  provider: string;
  model: string;
}

export function AgentTestPanel({ agentId }: { agentId: string }) {
  const [prompt, setPrompt] = useState("Xin chào, hãy giới thiệu ngắn gọn về bạn.");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function runTest() {
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/system-agents/${agentId}/test`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ prompt }),
      });
      const body = (await res.json().catch(() => null)) as
        | (TestResult & { detail?: string; error?: string })
        | null;
      if (!res.ok) {
        setError(body?.detail ?? body?.error ?? `HTTP ${res.status}`);
      } else if (body) {
        setResult(body);
      }
    } catch {
      setError("Không kết nối được tới runtime.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-3">
      <textarea
        value={prompt}
        onChange={(e) => setPrompt(e.target.value)}
        rows={3}
        className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1.5 text-sm"
        placeholder="Nhập prompt để test agent..."
      />
      <Button type="button" size="sm" onClick={runTest} disabled={loading || !prompt.trim()}>
        {loading ? "Đang chạy..." : "Test agent"}
      </Button>
      {error && (
        <p className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
      )}
      {result && (
        <div className="space-y-1 rounded border border-emerald-200 bg-emerald-50 p-3 text-sm">
          <div className="text-[10px] uppercase tracking-wide text-emerald-700">
            {result.provider} · {result.model}
          </div>
          <p className="whitespace-pre-wrap">{result.final_answer}</p>
        </div>
      )}
    </div>
  );
}
