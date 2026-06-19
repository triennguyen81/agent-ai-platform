"use client";

import { useState } from "react";
import { Loader2, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/input";

type Tab = "vat" | "pit" | "cit";

interface AgentRunResponse {
  final_answer: string;
}

const VAT_RATES = [0, 5, 8, 10];
const CIT_RATES = [5, 10, 15, 17, 20];

export default function TaxCalcPage() {
  const [tab, setTab] = useState<Tab>("vat");

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Calculator thuế</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          3 loại thuế phổ biến nhất với DN Việt Nam. Backend gọi
          <code className="mx-1 rounded bg-[var(--color-muted)] px-1.5 py-0.5 text-xs">
            calculate_vat / calculate_pit / calculate_cit
          </code>
          qua /agent/run.
        </p>
      </header>

      <div className="flex gap-1 rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] p-1 text-sm">
        {(["vat", "pit", "cit"] as Tab[]).map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={`flex-1 rounded-full px-4 py-1.5 transition ${
              tab === t
                ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
            }`}
          >
            {t === "vat" ? "VAT (GTGT)" : t === "pit" ? "TNCN" : "TNDN"}
          </button>
        ))}
      </div>

      {tab === "vat" && <VatForm />}
      {tab === "pit" && <PitForm />}
      {tab === "cit" && <CitForm />}
    </div>
  );
}

function CalcShell({
  children,
  result,
  loading,
  error,
}: {
  children: React.ReactNode;
  result: string;
  loading: boolean;
  error: string | null;
}) {
  return (
    <div className="grid gap-4 lg:grid-cols-[360px_1fr]">
      <div className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        {children}
      </div>
      <div className="min-h-[200px] rounded-[var(--radius)] border bg-[var(--color-muted)] p-5">
        {loading ? (
          <div className="flex items-center gap-2 text-sm text-[var(--color-muted-fg)]">
            <Loader2 className="size-4 animate-spin" />
            Agent đang tính…
          </div>
        ) : error ? (
          <p className="text-sm text-red-700">{error}</p>
        ) : result ? (
          <div className="prose prose-sm max-w-none text-[var(--color-fg)] [&_h2]:font-serif [&_h3]:font-semibold [&_p]:my-1.5 [&_table]:my-2 [&_table]:w-full [&_th]:border [&_th]:border-[var(--color-border)] [&_th]:bg-[var(--color-bg)] [&_th]:px-2 [&_th]:py-1 [&_th]:text-left [&_td]:border [&_td]:border-[var(--color-border)] [&_td]:px-2 [&_td]:py-1 [&_ul]:my-2 [&_ul]:pl-5">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{result}</ReactMarkdown>
          </div>
        ) : (
          <p className="text-sm text-[var(--color-muted-fg)]">
            Nhập số liệu bên trái và bấm <strong>Tính</strong>.
          </p>
        )}
      </div>
    </div>
  );
}

async function runAgent(task: string): Promise<string> {
  const res = await fetch("/api/agent/run", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ task, max_turns: 3 }),
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`HTTP ${res.status}: ${text.slice(0, 200)}`);
  }
  const body = (await res.json()) as AgentRunResponse;
  return body.final_answer;
}

function VatForm() {
  const [amount, setAmount] = useState("110000000");
  const [rate, setRate] = useState(10);
  const [inclusive, setInclusive] = useState(true);
  const [result, setResult] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setLoading(true);
    setError(null);
    try {
      const task = `Tính VAT bằng tool calculate_vat: amount=${amount}, rate=${rate}, inclusive=${inclusive}. Trả về breakdown ngắn gọn.`;
      const answer = await runAgent(task);
      setResult(answer);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(false);
    }
  }

  return (
    <CalcShell result={result} loading={loading} error={error}>
      <div className="space-y-1.5">
        <Label htmlFor="amount">Số tiền (VND)</Label>
        <Input
          id="amount"
          type="text"
          inputMode="numeric"
          value={amount}
          onChange={(e) => setAmount(e.target.value.replace(/\D/g, ""))}
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="rate">Thuế suất</Label>
        <div className="flex gap-1">
          {VAT_RATES.map((r) => (
            <button
              key={r}
              type="button"
              onClick={() => setRate(r)}
              className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-sm ${
                rate === r
                  ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                  : "border-[var(--color-border)] hover:border-[var(--color-primary)]"
              }`}
            >
              {r}%
            </button>
          ))}
        </div>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="inclusive">Số tiền nhập:</Label>
        <div className="flex gap-1">
          <button
            type="button"
            onClick={() => setInclusive(true)}
            className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-xs ${
              inclusive
                ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "border-[var(--color-border)]"
            }`}
          >
            Đã gồm VAT
          </button>
          <button
            type="button"
            onClick={() => setInclusive(false)}
            className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-xs ${
              !inclusive
                ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "border-[var(--color-border)]"
            }`}
          >
            Chưa gồm VAT
          </button>
        </div>
      </div>
      <Button onClick={submit} disabled={loading || !amount} className="w-full">
        <Wand2 className="size-4" /> Tính
      </Button>
    </CalcShell>
  );
}

function PitForm() {
  const [gross, setGross] = useState("30000000");
  const [deps, setDeps] = useState(0);
  const [includeSi, setIncludeSi] = useState(true);
  const [result, setResult] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setLoading(true);
    setError(null);
    try {
      const task = `Tính TNCN bằng tool calculate_pit: gross_monthly=${gross}, dependents=${deps}, include_si=${includeSi}. Show breakdown từng bậc.`;
      const answer = await runAgent(task);
      setResult(answer);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(false);
    }
  }

  return (
    <CalcShell result={result} loading={loading} error={error}>
      <div className="space-y-1.5">
        <Label htmlFor="gross">Lương gross (VND/tháng)</Label>
        <Input
          id="gross"
          type="text"
          inputMode="numeric"
          value={gross}
          onChange={(e) => setGross(e.target.value.replace(/\D/g, ""))}
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="deps">Số người phụ thuộc</Label>
        <Input
          id="deps"
          type="number"
          min={0}
          max={20}
          value={deps}
          onChange={(e) => setDeps(Math.max(0, Number(e.target.value)))}
        />
        <p className="text-[10px] text-[var(--color-muted-fg)]">
          NPT phải đã đăng ký với cơ quan thuế để được giảm trừ 4,4 triệu/người.
        </p>
      </div>
      <div className="space-y-1.5">
        <Label>BHXH/BHYT/BHTN (10,5%)</Label>
        <div className="flex gap-1">
          <button
            type="button"
            onClick={() => setIncludeSi(true)}
            className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-xs ${
              includeSi
                ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "border-[var(--color-border)]"
            }`}
          >
            Có đóng
          </button>
          <button
            type="button"
            onClick={() => setIncludeSi(false)}
            className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-xs ${
              !includeSi
                ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                : "border-[var(--color-border)]"
            }`}
          >
            Không đóng
          </button>
        </div>
      </div>
      <Button onClick={submit} disabled={loading || !gross} className="w-full">
        <Wand2 className="size-4" /> Tính
      </Button>
    </CalcShell>
  );
}

function CitForm() {
  const [revenue, setRevenue] = useState("50000000000");
  const [expense, setExpense] = useState("38000000000");
  const [loss, setLoss] = useState("0");
  const [ratePct, setRatePct] = useState(20);
  const [result, setResult] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setLoading(true);
    setError(null);
    try {
      const task = `Tính TNDN bằng tool calculate_cit: revenue=${revenue}, expense=${expense}, loss_carryforward=${loss}, rate_pct=${ratePct}.`;
      const answer = await runAgent(task);
      setResult(answer);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(false);
    }
  }

  return (
    <CalcShell result={result} loading={loading} error={error}>
      <div className="space-y-1.5">
        <Label htmlFor="revenue">Doanh thu (VND/năm)</Label>
        <Input
          id="revenue"
          type="text"
          inputMode="numeric"
          value={revenue}
          onChange={(e) => setRevenue(e.target.value.replace(/\D/g, ""))}
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="expense">Chi phí được trừ</Label>
        <Input
          id="expense"
          type="text"
          inputMode="numeric"
          value={expense}
          onChange={(e) => setExpense(e.target.value.replace(/\D/g, ""))}
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="loss">Lỗ kết chuyển</Label>
        <Input
          id="loss"
          type="text"
          inputMode="numeric"
          value={loss}
          onChange={(e) => setLoss(e.target.value.replace(/\D/g, ""))}
        />
      </div>
      <div className="space-y-1.5">
        <Label>Thuế suất</Label>
        <div className="flex flex-wrap gap-1">
          {CIT_RATES.map((r) => (
            <button
              key={r}
              type="button"
              onClick={() => setRatePct(r)}
              className={`flex-1 rounded-[var(--radius)] border px-2 py-1.5 text-sm ${
                ratePct === r
                  ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                  : "border-[var(--color-border)]"
              }`}
            >
              {r}%
            </button>
          ))}
        </div>
        <p className="text-[10px] text-[var(--color-muted-fg)]">
          20% mặc định · 17% SME · 15% siêu nhỏ · 10% công nghệ cao · 5-10% khu
          ưu đãi
        </p>
      </div>
      <Button onClick={submit} disabled={loading || !revenue} className="w-full">
        <Wand2 className="size-4" /> Tính
      </Button>
    </CalcShell>
  );
}
