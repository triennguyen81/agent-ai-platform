"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { CheckCircle2, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Sprint 4 gap UC1 — bấm để book hoá đơn bán vào sim_ar_ledger + tăng
// customer.current_ar. Idempotent theo voucher_id.
interface AcceptInvoiceButtonProps {
  voucherId: string;
  disabled?: boolean;
  // Optional — for callers that fetch voucher state client-side (not via a
  // Server Component's own props) and so can't rely on router.refresh()
  // alone to pick up the new POSTED status; e.g. the Accounting → Hoá đơn
  // page. Voucher detail page doesn't pass this — router.refresh() already
  // re-runs its server fetch.
  onApplied?: () => void;
}

interface ApplyResponse {
  status: string;
  payment_status?: string;
  previous_balance?: number;
  new_balance?: number;
  invoice_total?: number;
  ledger_rows_inserted?: number;
  error?: string;
}

const fmt = (n: number | undefined): string =>
  n === undefined || n === null ? "—" : n.toLocaleString("vi-VN");

export function AcceptInvoiceButton({ voucherId, disabled, onApplied }: AcceptInvoiceButtonProps) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<ApplyResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading || disabled) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/apply-invoice`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
      });
      const body = (await res.json()) as ApplyResponse;
      if (!res.ok) {
        setError(body.error ?? `HTTP ${res.status}`);
        return;
      }
      setResult(body);
      onApplied?.();
      setTimeout(() => router.refresh(), 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setLoading(false);
    }
  };

  if (result?.status === "applied") {
    return (
      <div className="space-y-2 rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-4 py-3 text-sm text-emerald-900">
        <div className="flex items-center gap-2 font-medium">
          <CheckCircle2 className="size-4" /> Đã ghi nhận phải thu: {fmt(result.invoice_total)}đ
        </div>
        <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs font-mono">
          <div>Công nợ trước (131): <b>{fmt(result.previous_balance)}</b></div>
          <div>Giá trị HĐ: <b>{fmt(result.invoice_total)}</b></div>
          <div>
            Công nợ sau (131): <b className="text-emerald-800">{fmt(result.new_balance)}</b>
          </div>
          <div>Trạng thái thanh toán: <b>Chưa thu (UNPAID)</b></div>
        </div>
        <div className="text-[10px] italic text-emerald-700">
          Hoá đơn đã POSTED — chưa ghi nhận đã thu tiền. Trang tự refresh…
        </div>
      </div>
    );
  }

  if (result?.status === "already_applied") {
    return (
      <div className="rounded-[var(--radius)] border border-blue-200 bg-blue-50 px-4 py-2 text-sm text-blue-800">
        Hoá đơn này đã được ghi vào công nợ trước đó.
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <Button onClick={submit} disabled={loading || disabled} className="w-full sm:w-auto">
        {loading ? (
          <>
            <Loader2 className="size-4 animate-spin" /> Đang ghi nhận…
          </>
        ) : (
          <>
            <CheckCircle2 className="size-4" /> Duyệt &amp; ghi nhận
          </>
        )}
      </Button>
      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          Lỗi: {error}
        </p>
      )}
    </div>
  );
}
