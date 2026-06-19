"use client";

import { useState } from "react";
import { Download, Loader2, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Input, Label, Textarea } from "@/components/ui/input";

type ReportType = "bckqhdkd" | "bcdkt" | "lctt";

interface AgentRunResponse {
  final_answer: string;
}

const REPORT_NAMES: Record<ReportType, string> = {
  bckqhdkd: "Báo cáo kết quả HĐKD (BCKQHĐKD)",
  bcdkt: "Bảng cân đối kế toán (BCĐKT)",
  lctt: "Báo cáo lưu chuyển tiền tệ (LCTT)",
};

const REPORT_HINTS: Record<ReportType, string> = {
  bckqhdkd:
    "Doanh thu thuần, giá vốn, LN gộp, CP bán hàng, CP QLDN, LN từ HĐKD, thu nhập khác, LN trước thuế, thuế TNDN, LN sau thuế.",
  bcdkt:
    "Tài sản ngắn hạn (tiền, phải thu, hàng tồn kho), tài sản dài hạn (TSCĐ, đầu tư), nợ phải trả (vay, phải trả NB, thuế), vốn CSH.",
  lctt:
    "3 luồng: HĐKD (LN trước thuế ± khoản điều chỉnh), HĐ đầu tư (mua/bán TSCĐ), HĐ tài chính (vay, trả nợ, cổ tức).",
};

export default function ReportsPage() {
  const [type, setType] = useState<ReportType>("bckqhdkd");
  const [companyName, setCompanyName] = useState("Công ty TNHH Cát Tường AI");
  const [period, setPeriod] = useState("Năm 2025");
  const [contextData, setContextData] = useState("");
  const [report, setReport] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function generate() {
    setLoading(true);
    setError(null);
    try {
      const task = `Lập khung mẫu cho **${REPORT_NAMES[type]}** áp dụng TT200/2014/TT-BTC.

**Doanh nghiệp:** ${companyName}
**Kỳ báo cáo:** ${period}

${contextData.trim() ? `**Dữ liệu / số liệu user cung cấp:**\n\n${contextData.trim()}\n\n` : ""}**Yêu cầu:**
1. Lập bảng đầy đủ các chỉ tiêu (Mã số, Tên chỉ tiêu, Số năm này, Số năm trước).
2. Nếu có dữ liệu user cung cấp → điền vào tương ứng; còn lại để "—".
3. Tham chiếu: ${REPORT_HINTS[type]}
4. Cuối bảng có dòng ký xác nhận (Người lập / Kế toán trưởng / Giám đốc).
5. Format Markdown đẹp, export được sang Word.`;
      const res = await fetch("/api/agent/run", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ task, max_turns: 4 }),
      });
      if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${(await res.text()).slice(0, 200)}`);
      }
      const body = (await res.json()) as AgentRunResponse;
      setReport(body.final_answer);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(false);
    }
  }

  function downloadMarkdown() {
    if (!report) return;
    const blob = new Blob([report], { type: "text/markdown;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${type}-${period.replace(/\s+/g, "_")}.md`;
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Báo cáo tài chính</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Sinh khung BCKQHĐKD, BCĐKT, LCTT theo TT200. Bạn cung cấp số liệu chính
          → AI sắp xếp vào bảng chuẩn.
        </p>
      </header>

      <div className="grid gap-4 lg:grid-cols-[360px_1fr]">
        <div className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <div className="space-y-1.5">
            <Label>Loại báo cáo</Label>
            <div className="space-y-1">
              {(Object.keys(REPORT_NAMES) as ReportType[]).map((t) => (
                <button
                  key={t}
                  type="button"
                  onClick={() => setType(t)}
                  className={`w-full rounded-[var(--radius)] border px-3 py-2 text-left text-sm ${
                    type === t
                      ? "border-[var(--color-primary)] bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                      : "border-[var(--color-border)] hover:border-[var(--color-primary)]"
                  }`}
                >
                  {REPORT_NAMES[t]}
                </button>
              ))}
            </div>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="company">Tên doanh nghiệp</Label>
            <Input
              id="company"
              value={companyName}
              onChange={(e) => setCompanyName(e.target.value)}
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="period">Kỳ báo cáo</Label>
            <Input
              id="period"
              value={period}
              onChange={(e) => setPeriod(e.target.value)}
              placeholder="vd: Q3/2025, Năm 2025"
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="data">
              Dữ liệu / số liệu (tuỳ chọn)
            </Label>
            <Textarea
              id="data"
              rows={6}
              value={contextData}
              onChange={(e) => setContextData(e.target.value)}
              placeholder={`Paste hoặc gõ số liệu bạn có. Ví dụ:\nDoanh thu thuần: 50 tỷ\nGiá vốn: 30 tỷ\nCP bán hàng: 5 tỷ\nCP QLDN: 4 tỷ\n...`}
            />
            <p className="text-[10px] text-[var(--color-muted-fg)]">
              Không cần đầy đủ — AI sẽ điền vào chỉ tiêu tương ứng, còn lại để
              trống.
            </p>
          </div>

          <div className="flex gap-2">
            <Button onClick={generate} disabled={loading} className="flex-1">
              {loading ? <Loader2 className="size-4 animate-spin" /> : <Wand2 className="size-4" />}
              Sinh báo cáo
            </Button>
            {report && (
              <Button variant="outline" onClick={downloadMarkdown}>
                <Download className="size-4" />
              </Button>
            )}
          </div>

          {error && (
            <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
              {error}
            </p>
          )}
        </div>

        <div className="min-h-[400px] rounded-[var(--radius)] border bg-[var(--color-muted)] p-5">
          {report ? (
            <div className="prose prose-sm max-w-none [&_h1]:font-serif [&_h2]:font-serif [&_table]:my-2 [&_table]:w-full [&_table]:text-xs [&_th]:border [&_th]:bg-[var(--color-bg)] [&_th]:px-2 [&_th]:py-1 [&_th]:text-left [&_td]:border [&_td]:px-2 [&_td]:py-1">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{report}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Khung báo cáo sẽ hiện ở đây sau khi sinh.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
