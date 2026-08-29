"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// Sprint 4 gap UC2 — rollback receipt đã áp: DELETE ledger rows + recompute
// customer.current_ar. Confirmation dialog trước khi execute vì destructive.
interface UnapplyReceiptButtonProps {
  voucherId: string;
  disabled?: boolean;
}

interface UnapplyResponse {
  status: string;
  previous_balance?: number;
  new_balance?: number;
  ledger_rows_deleted?: number;
  error?: string;
}

const fmt = (n: number | undefined): string =>
  n === undefined || n === null ? "—" : n.toLocaleString("vi-VN");

export function UnapplyReceiptButton({ voucherId, disabled }: UnapplyReceiptButtonProps) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<UnapplyResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (loading || disabled) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/unapply-receipt`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
      });
      const body = (await res.json()) as UnapplyResponse;
      if (!res.ok) {
        setError(body.error ?? `HTTP ${res.status}`);
        return;
      }
      setResult(body);
      setConfirming(false);
      setTimeout(() => router.refresh(), 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setLoading(false);
    }
  };

  if (result?.status === "unapplied") {
    return (
      <div className="space-y-2 rounded-[var(--radius)] border border-amber-300 bg-amber-100 px-4 py-3 text-sm text-amber-900">
        <div className="flex items-center gap-2 font-medium">
          <Undo2 className="size-4" /> Đã huỷ áp thu — công nợ khôi phục
        </div>
        <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs font-mono">
          <div>Công nợ trước huỷ: <b>{fmt(result.previous_balance)}</b></div>
          <div>Công nợ sau huỷ: <b>{fmt(result.new_balance)}</b></div>
          <div>Rows đã xoá: <b>{result.ledger_rows_deleted ?? 0}</b></div>
        </div>
        <div className="text-[10px] italic text-amber-700">
          Trạng thái voucher rollback về 'extracted'. Trang tự refresh…
        </div>
      </div>
    );
  }

  if (confirming) {
    return (
      <div className="space-y-2 rounded-[var(--radius)] border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-900">
        <p className="font-medium">⚠ Xác nhận huỷ áp thu?</p>
        <p className="text-xs">
          Sẽ xoá tất cả credit rows đã ghi từ voucher này khỏi sim_ar_ledger và tính lại
          current_ar. Voucher chuyển về trạng thái 'extracted' để có thể apply lại sau.
        </p>
        <div className="flex gap-2">
          <Button variant="destructive" onClick={submit} disabled={loading}>
            {loading ? (
              <>
                <Loader2 className="size-4 animate-spin" /> Đang huỷ…
              </>
            ) : (
              <>
                <Undo2 className="size-4" /> Xác nhận huỷ
              </>
            )}
          </Button>
          <Button variant="outline" onClick={() => setConfirming(false)} disabled={loading}>
            Không
          </Button>
        </div>
        {error && (
          <p className="text-xs text-red-700">Lỗi: {error}</p>
        )}
      </div>
    );
  }

  return (
    <Button variant="outline" onClick={() => setConfirming(true)} disabled={disabled}>
      <Undo2 className="size-4" /> Huỷ áp thu
    </Button>
  );
}
