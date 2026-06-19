"use client";

import { useState } from "react";
import { Loader2, Plus, Trash2, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

interface EntryLine {
  id: string;
  side: "Nợ" | "Có";
  account: string;
  amount: string;
  description: string;
}

interface AgentRunResponse {
  final_answer: string;
}

function makeId(): string {
  return `l-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
}

function blankLine(): EntryLine {
  return { id: makeId(), side: "Nợ", account: "", amount: "", description: "" };
}

function parseAmount(s: string): number {
  const num = Number(s.replace(/\D/g, ""));
  return Number.isFinite(num) ? num : 0;
}

function formatVnd(n: number): string {
  if (!Number.isFinite(n)) return "0";
  return n.toLocaleString("vi-VN");
}

export default function JournalPage() {
  const [description, setDescription] = useState("Mua hàng nhập kho, VAT 10%, chưa thanh toán");
  const [lines, setLines] = useState<EntryLine[]>([
    { id: makeId(), side: "Nợ", account: "156", amount: "100000000", description: "Hàng hoá" },
    { id: makeId(), side: "Nợ", account: "133", amount: "10000000", description: "VAT đầu vào" },
    { id: makeId(), side: "Có", account: "331", amount: "110000000", description: "Phải trả NB" },
  ]);
  const [validationResult, setValidationResult] = useState("");
  const [aiResult, setAiResult] = useState("");
  const [loading, setLoading] = useState<"validate" | "suggest" | null>(null);
  const [error, setError] = useState<string | null>(null);

  function updateLine(id: string, patch: Partial<EntryLine>) {
    setLines((prev) => prev.map((l) => (l.id === id ? { ...l, ...patch } : l)));
  }

  function addLine() {
    setLines((prev) => [...prev, blankLine()]);
  }

  function removeLine(id: string) {
    setLines((prev) => prev.filter((l) => l.id !== id));
  }

  const totalDebit = lines
    .filter((l) => l.side === "Nợ")
    .reduce((sum, l) => sum + parseAmount(l.amount), 0);
  const totalCredit = lines
    .filter((l) => l.side === "Có")
    .reduce((sum, l) => sum + parseAmount(l.amount), 0);
  const balanced = totalDebit === totalCredit && totalDebit > 0;

  async function callAgent(task: string): Promise<string> {
    const res = await fetch("/api/agent/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ task, max_turns: 4 }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}: ${(await res.text()).slice(0, 200)}`);
    const body = (await res.json()) as AgentRunResponse;
    return body.final_answer;
  }

  async function validate() {
    setLoading("validate");
    setError(null);
    try {
      const linesJson = JSON.stringify(
        lines.map((l) => ({
          side: l.side,
          account: l.account,
          amount: parseAmount(l.amount),
          description: l.description,
        })),
      );
      const task = `Validate bút toán này dùng tool journal_entry_validator với lines=${linesJson}. Trả kết quả ngắn gọn (OK / lỗi gì).`;
      setValidationResult(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  async function suggest() {
    setLoading("suggest");
    setError(null);
    try {
      const task = `Cho nghiệp vụ: "${description}". Đề xuất bút toán chuẩn TT200 theo skill journal-entry-formatter. Trả về dạng list dòng Nợ/Có với TK, số tiền, mô tả. Không cần giải thích dài.`;
      setAiResult(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Định khoản</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Bảng nhập bút toán. AI gợi ý dựa trên skill <code>journal-entry-formatter</code>.
          Validate qua tool <code>journal_entry_validator</code>.
        </p>
      </header>

      <div className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <div className="space-y-1.5">
          <label className="text-sm font-medium" htmlFor="desc">
            Mô tả nghiệp vụ
          </label>
          <Input
            id="desc"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="vd: Mua hàng nhập kho, VAT 10%, chưa thanh toán cho NCC X"
          />
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-[var(--color-muted)] text-xs uppercase tracking-wide text-[var(--color-muted-fg)]">
                <th className="px-2 py-2 text-left">Vế</th>
                <th className="px-2 py-2 text-left">TK</th>
                <th className="px-2 py-2 text-right">Số tiền (VND)</th>
                <th className="px-2 py-2 text-left">Diễn giải</th>
                <th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => (
                <tr key={line.id} className="border-b">
                  <td className="px-2 py-2">
                    <select
                      value={line.side}
                      onChange={(e) =>
                        updateLine(line.id, { side: e.target.value as "Nợ" | "Có" })
                      }
                      className="rounded-[var(--radius)] border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-sm"
                    >
                      <option value="Nợ">Nợ</option>
                      <option value="Có">Có</option>
                    </select>
                  </td>
                  <td className="px-2 py-2">
                    <Input
                      value={line.account}
                      onChange={(e) =>
                        updateLine(line.id, { account: e.target.value.replace(/\D/g, "") })
                      }
                      placeholder="156"
                      className="w-20"
                    />
                  </td>
                  <td className="px-2 py-2 text-right">
                    <Input
                      value={
                        line.amount ? Number(line.amount.replace(/\D/g, "")).toLocaleString("vi-VN") : ""
                      }
                      onChange={(e) =>
                        updateLine(line.id, { amount: e.target.value.replace(/\D/g, "") })
                      }
                      className="text-right"
                    />
                  </td>
                  <td className="px-2 py-2">
                    <Input
                      value={line.description}
                      onChange={(e) =>
                        updateLine(line.id, { description: e.target.value })
                      }
                      placeholder="Hàng hoá"
                    />
                  </td>
                  <td className="px-2 py-2">
                    <button
                      type="button"
                      onClick={() => removeLine(line.id)}
                      className="text-[var(--color-muted-fg)] hover:text-red-600"
                      aria-label="Xoá dòng"
                    >
                      <Trash2 className="size-4" />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="bg-[var(--color-muted)] text-sm font-semibold">
                <td className="px-2 py-2" colSpan={2}>
                  Tổng
                </td>
                <td className="px-2 py-2 text-right">
                  <div>Nợ: {formatVnd(totalDebit)} ₫</div>
                  <div>Có: {formatVnd(totalCredit)} ₫</div>
                </td>
                <td className="px-2 py-2" colSpan={2}>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs ${
                      balanced
                        ? "bg-emerald-100 text-emerald-700"
                        : totalDebit === 0 && totalCredit === 0
                        ? "bg-gray-100 text-gray-600"
                        : "bg-red-100 text-red-700"
                    }`}
                  >
                    {balanced
                      ? "✓ Cân đối"
                      : totalDebit === 0 && totalCredit === 0
                      ? "—"
                      : `Lệch ${formatVnd(Math.abs(totalDebit - totalCredit))} ₫`}
                  </span>
                </td>
              </tr>
            </tfoot>
          </table>
        </div>

        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={addLine}>
            <Plus className="size-4" /> Thêm dòng
          </Button>
          <Button size="sm" onClick={validate} disabled={loading !== null || lines.length === 0}>
            {loading === "validate" ? <Loader2 className="size-4 animate-spin" /> : null}
            Validate
          </Button>
          <Button size="sm" variant="outline" onClick={suggest} disabled={loading !== null || !description}>
            {loading === "suggest" ? <Loader2 className="size-4 animate-spin" /> : <Wand2 className="size-4" />}
            AI gợi ý
          </Button>
        </div>

        {error && (
          <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <p className="font-medium">Validation result</p>
          {validationResult ? (
            <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{validationResult}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Bấm <strong>Validate</strong> để gọi tool kiểm tra cân đối + TK đúng TT200.
            </p>
          )}
        </div>

        <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <p className="font-medium">AI gợi ý</p>
          {aiResult ? (
            <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{aiResult}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Mô tả nghiệp vụ ở ô trên rồi bấm <strong>AI gợi ý</strong>.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
