"use client";

import { useCallback, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { ArrowLeft, ImageIcon, Loader2, Upload } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/input";

interface UploadResponse {
  voucherId: string;
  status: string;
  ocrConfidence?: number;
  agentResponse?: string;
  error?: string;
}

interface Row {
  file: File;
  status: "pending" | "uploading" | "done" | "error";
  voucherId?: string;
  message?: string;
}

const VOUCHER_TYPES: { value: string; label: string }[] = [
  { value: "invoice_vat", label: "Hoá đơn VAT điện tử" },
  { value: "receipt", label: "Phiếu thu" },
  { value: "payment", label: "Phiếu chi" },
  { value: "bank_transfer", label: "Uỷ nhiệm chi" },
  { value: "bank_statement", label: "Sao kê ngân hàng" },
];

const ACCEPTED = "image/jpeg,image/png,image/webp,image/bmp,application/pdf";

export default function VoucherUploadPage() {
  const router = useRouter();
  const [voucherType, setVoucherType] = useState<string>("invoice_vat");
  const [rows, setRows] = useState<Row[]>([]);
  const [running, setRunning] = useState(false);
  const [lastResult, setLastResult] = useState<string | null>(null);

  const onFiles = useCallback((files: FileList | null) => {
    if (!files || files.length === 0) return;
    const additions: Row[] = Array.from(files).map((f) => ({ file: f, status: "pending" }));
    setRows((prev) => [...prev, ...additions]);
  }, []);

  async function runOne(index: number, row: Row): Promise<Row> {
    const fd = new FormData();
    fd.append("file", row.file, row.file.name);
    const res = await fetch(`/api/vouchers/upload?voucherType=${encodeURIComponent(voucherType)}`, {
      method: "POST",
      body: fd,
    });
    if (!res.ok) {
      const text = await res.text();
      return { ...row, status: "error", message: `HTTP ${res.status}: ${text.slice(0, 200)}` };
    }
    const body = (await res.json()) as UploadResponse;
    if (body.status === "rejected") {
      return {
        ...row,
        status: "error",
        voucherId: body.voucherId,
        message: body.error ?? "OCR thất bại",
      };
    }
    return {
      ...row,
      status: "done",
      voucherId: body.voucherId,
      message: body.agentResponse?.slice(0, 400) ?? "OK",
    };
  }

  async function runAll() {
    if (running) return;
    setRunning(true);
    setLastResult(null);
    try {
      for (let i = 0; i < rows.length; i++) {
        if (rows[i].status !== "pending") continue;
        setRows((prev) => prev.map((r, idx) => (idx === i ? { ...r, status: "uploading" } : r)));
        const updated = await runOne(i, rows[i]);
        setRows((prev) => prev.map((r, idx) => (idx === i ? updated : r)));
        if (updated.status === "done" && updated.message) {
          setLastResult(updated.message);
        }
      }
      setTimeout(() => router.refresh(), 800);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <Link
          href="/vouchers"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Tất cả chứng từ
        </Link>
      </div>

      <header className="space-y-2">
        <h1 className="font-serif text-3xl">Thêm chứng từ (ảnh / PDF)</h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Upload ảnh hoá đơn VAT, phiếu thu/chi, uỷ nhiệm chi hoặc sao kê. AI tự OCR
          + trích xuất trường + đề xuất bút toán N/C theo TT200.
        </p>
      </header>

      <div className="grid gap-4 lg:grid-cols-[340px_1fr]">
        <div className="space-y-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-5">
          <div className="space-y-1.5">
            <Label htmlFor="vtype">Loại chứng từ</Label>
            <select
              id="vtype"
              value={voucherType}
              onChange={(e) => setVoucherType(e.target.value)}
              className="w-full rounded-[var(--radius)] border px-3 py-2 text-sm"
              disabled={running}
            >
              {VOUCHER_TYPES.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
          </div>

          <Button
            onClick={runAll}
            disabled={running || rows.filter((r) => r.status === "pending").length === 0}
            className="w-full"
          >
            {running ? (
              <>
                <Loader2 className="size-4 animate-spin" /> Đang xử lý…
              </>
            ) : (
              <>
                <Upload className="size-4" /> Chạy OCR + AI extract
              </>
            )}
          </Button>

          <p className="text-[10px] text-[var(--color-muted-fg)]">
            Pipeline: image → PaddleOCR → skill <code>voucher-invoice-vat</code> →
            <code>save_voucher</code> → <code>suggest_journal_entry</code>. Mỗi ảnh ~10-15s.
          </p>
        </div>

        <div className="space-y-3">
          <label
            htmlFor="file-input"
            className="flex cursor-pointer flex-col items-center justify-center gap-2 rounded-[var(--radius)] border-2 border-dashed border-[var(--color-muted-fg)] bg-[var(--color-muted)]/30 py-10 hover:bg-[var(--color-muted)]/60"
          >
            <ImageIcon className="size-10 text-[var(--color-muted-fg)]" />
            <span className="text-sm font-medium">Kéo thả ảnh vào đây, hoặc bấm để chọn</span>
            <span className="text-xs text-[var(--color-muted-fg)]">
              Chấp nhận: JPG, PNG, WebP, BMP, PDF (multi-file)
            </span>
            <input
              id="file-input"
              type="file"
              accept={ACCEPTED}
              multiple
              className="hidden"
              onChange={(e) => onFiles(e.target.files)}
              disabled={running}
            />
          </label>

          {rows.length > 0 && (
            <div className="overflow-hidden rounded-[var(--radius)] border">
              <table className="w-full text-xs">
                <thead className="bg-[var(--color-muted)] text-[10px] uppercase tracking-wider text-[var(--color-muted-fg)]">
                  <tr>
                    <th className="px-3 py-2 text-left">File</th>
                    <th className="px-3 py-2 text-right">Size</th>
                    <th className="px-3 py-2 text-center">Status</th>
                    <th className="px-3 py-2 text-left">Kết quả</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, idx) => (
                    <tr key={idx} className="border-t">
                      <td className="px-3 py-2 font-mono">{r.file.name}</td>
                      <td className="px-3 py-2 text-right">{(r.file.size / 1024).toFixed(0)} KB</td>
                      <td className="px-3 py-2 text-center">
                        {r.status === "pending" && (
                          <span className="text-[var(--color-muted-fg)]">…</span>
                        )}
                        {r.status === "uploading" && (
                          <Loader2 className="mx-auto size-3.5 animate-spin text-amber-600" />
                        )}
                        {r.status === "done" && <span className="text-emerald-700">✓</span>}
                        {r.status === "error" && <span className="text-red-700">✗</span>}
                      </td>
                      <td className="px-3 py-2">
                        {r.voucherId ? (
                          <Link
                            href={`/vouchers/${r.voucherId}`}
                            className="text-[var(--color-primary)] hover:underline"
                          >
                            {r.voucherId.slice(0, 8)}…
                          </Link>
                        ) : (
                          <span className="text-[var(--color-muted-fg)]">
                            {r.message ?? "—"}
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {lastResult && (
            <div className="space-y-2 rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 p-4">
              <p className="font-medium text-emerald-700">Kết quả AI (chứng từ cuối)</p>
              <div className="prose prose-sm max-w-none [&_p]:my-1 [&_ul]:my-1 [&_ul]:pl-5">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>{lastResult}</ReactMarkdown>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
