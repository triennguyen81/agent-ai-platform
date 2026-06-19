"use client";

import { useState } from "react";
import { Loader2, Wand2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/input";

interface AgentRunResponse {
  final_answer: string;
}

const SAMPLE = `Hoá đơn GTGT
Số: 0000123 - Ký hiệu: 1C24TYY
Ngày: 15/11/2025
Người bán: Công ty TNHH Vật tư ABC
MST: 0312345678
Người mua: Công ty TNHH Cát Tường AI
MST: 0399999999

| STT | Tên hàng hoá | ĐVT | SL | Đơn giá | Thành tiền |
|---|---|---|---|---|---|
| 1 | Bút bi xanh | Cái | 50 | 5.000 | 250.000 |
| 2 | Giấy A4 | Ream | 20 | 65.000 | 1.300.000 |
| 3 | Mực in HP | Hộp | 5 | 850.000 | 4.250.000 |

Tổng tiền hàng (chưa VAT): 5.800.000 đ
Thuế suất VAT: 10%
Tiền thuế VAT: 580.000 đ
Tổng tiền thanh toán: 6.380.000 đ

(Bằng chữ: Sáu triệu ba trăm tám mươi nghìn đồng chẵn)
Phương thức thanh toán: Chuyển khoản`;

export default function InvoicePage() {
  const [rawText, setRawText] = useState(SAMPLE);
  const [extracted, setExtracted] = useState("");
  const [journalSuggest, setJournalSuggest] = useState("");
  const [loading, setLoading] = useState<"extract" | "journal" | null>(null);
  const [error, setError] = useState<string | null>(null);

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

  async function extract() {
    setLoading("extract");
    setError(null);
    try {
      const task = `Đây là nội dung hoá đơn:

---
${rawText}
---

Trích xuất các trường sau dạng bảng Markdown:
- Số hoá đơn / ký hiệu
- Ngày hoá đơn
- Người bán + MST
- Người mua + MST
- Tổng tiền chưa VAT
- Thuế suất VAT
- Tiền VAT
- Tổng thanh toán
- Phương thức thanh toán

Sau bảng trích xuất, đề xuất loại hàng (hàng hoá / NVL / CCDC / TSCĐ / chi phí) để định khoản đúng TK.`;
      setExtracted(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  async function buildJournal() {
    setLoading("journal");
    setError(null);
    try {
      const task = `Đây là nội dung hoá đơn:

---
${rawText}
---

Đề xuất bút toán định khoản chuẩn TT200. Áp dụng skill journal-entry-formatter:
- Format 2-3 dòng riêng cho mỗi vế Nợ / Có
- Tách dòng VAT (TK 133 đầu vào) bắt buộc
- Chọn TK chính phù hợp: 156 nếu hàng hoá để bán, 152 nếu NVL sản xuất, 153 nếu CCDC, 211 nếu TSCĐ, 642 nếu chi phí QLDN, 641 nếu chi phí bán hàng
- Nếu thanh toán chuyển khoản → Có TK 112; nếu chưa thanh toán → Có TK 331

Trả về bút toán đầy đủ kèm cơ sở TT200.`;
      setJournalSuggest(await callAgent(task));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Lỗi");
    } finally {
      setLoading(null);
    }
  }

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Hoá đơn → định khoản</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Paste nội dung hoá đơn (copy từ PDF / scan OCR / nhập tay) → AI trích
          xuất các trường + đề xuất bút toán chuẩn TT200.
        </p>
      </header>

      <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
        <label className="text-sm font-medium">Nội dung hoá đơn</label>
        <Textarea
          rows={14}
          value={rawText}
          onChange={(e) => setRawText(e.target.value)}
          className="font-mono text-xs"
        />
        <p className="text-[10px] text-[var(--color-muted-fg)]">
          OCR thật chưa wire — dán văn bản hoặc paste output từ Adobe / Foxit
          (Copy as Text).
        </p>
        <div className="flex gap-2">
          <Button onClick={extract} disabled={loading !== null || !rawText}>
            {loading === "extract" ? (
              <Loader2 className="size-4 animate-spin" />
            ) : (
              <Wand2 className="size-4" />
            )}
            Trích xuất trường
          </Button>
          <Button variant="outline" onClick={buildJournal} disabled={loading !== null || !rawText}>
            {loading === "journal" ? (
              <Loader2 className="size-4 animate-spin" />
            ) : (
              <Wand2 className="size-4" />
            )}
            Đề xuất bút toán
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
          <p className="font-medium">Trích xuất trường</p>
          {extracted ? (
            <div className="prose prose-sm max-w-none [&_table]:my-2 [&_table]:w-full [&_th]:border [&_th]:bg-[var(--color-bg)] [&_th]:px-2 [&_th]:py-1 [&_td]:border [&_td]:px-2 [&_td]:py-1">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{extracted}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Chưa có dữ liệu — bấm <strong>Trích xuất trường</strong>.
            </p>
          )}
        </div>

        <div className="space-y-2 rounded-[var(--radius)] border bg-[var(--color-muted)] p-4">
          <p className="font-medium">Bút toán đề xuất</p>
          {journalSuggest ? (
            <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{journalSuggest}</ReactMarkdown>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-fg)]">
              Chưa có dữ liệu — bấm <strong>Đề xuất bút toán</strong>.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
