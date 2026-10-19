"use client";

import { useState } from "react";
import { TAX_ERROR_LABEL } from "@/lib/tax-verification";
import { useRouter } from "next/navigation";
import { CheckCircle2, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

// AcceptExpenseButton — duyệt hoá đơn mua / đề nghị hoàn ứng (UC3). Phiếu chi/UNC dùng CashSettlementPanel.
// Handles 3 voucher_type variants với wording khác nhau:
//   invoice_vat_in → "Chấp nhận HĐ NCC · Ghi công nợ 331"
//   payment/bank_transfer → "Chấp nhận chi · Áp thu vào công nợ NCC"
//   expense_claim (nothing to pay) → "Duyệt cuối · Không phát sinh chi" (recognition happened at stage 2)
interface AcceptExpenseButtonProps {
  voucherId: string;
  voucherType: string;
  disabled?: boolean;
  // Optional — see AcceptInvoiceButtonProps.onApplied.
  onApplied?: () => void;
}

interface ApplyResponse {
  status: string;
  voucher_type?: string;
  // invoice_vat_in fields
  invoice_total?: number;
  // payment/bank_transfer fields
  total_applied?: number;
  discount_amount?: number;
  // shared AP balance fields
  previous_balance?: number;
  new_balance?: number;
  ledger_rows_inserted?: number;
  // expense_claim fields
  previous_advance?: number;
  new_advance?: number;
  previous_ytd_expense?: number;
  new_ytd_expense?: number;
  // errors
  error?: string;
  message?: string;
}

const fmt = (n: number | undefined): string =>
  n === undefined || n === null ? "—" : n.toLocaleString("vi-VN");

const wording = (voucherType: string) => {
  switch (voucherType.toLowerCase()) {
    case "invoice_vat_in":
      return {
        button: "Duyệt & ghi nhận",
        loading: "Đang ghi nhận…",
        success: "Đã ghi nhận phải trả",
      };
    case "expense_claim":
      return {
        button: "Duyệt cuối · Không phát sinh chi",
        loading: "Đang hoàn ứng…",
        success: "Đã hoàn ứng nhân viên",
      };
    case "payment":
    case "bank_transfer":
    default:
      return {
        button: "Chấp nhận chi · Áp vào công nợ NCC",
        loading: "Đang áp chi vào công nợ NCC…",
        success: "Đã áp chi vào công nợ NCC",
      };
  }
};

export function AcceptExpenseButton({ voucherId, voucherType, disabled, onApplied }: AcceptExpenseButtonProps) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<ApplyResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const words = wording(voucherType);

  const submit = async () => {
    if (loading || disabled) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/apply-expense`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
      });
      const body = (await res.json()) as ApplyResponse;
      if (!res.ok) {
        setError((body.error && TAX_ERROR_LABEL[body.error]) ?? body.error ?? `HTTP ${res.status}`);
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
    const isClaim = result.voucher_type === "expense_claim";
    const isInvoiceIn = result.voucher_type === "invoice_vat_in";
    return (
      <div className="space-y-2 rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-4 py-3 text-sm text-emerald-900">
        <div className="flex items-center gap-2 font-medium">
          <CheckCircle2 className="size-4" />{" "}
          {isInvoiceIn ? `${words.success}: ${fmt(result.invoice_total)}đ` : words.success}
        </div>
        {isClaim ? (
          <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs font-mono">
            <div>Tạm ứng trước: <b>{fmt(result.previous_advance)}</b></div>
            <div>Tạm ứng sau: <b>{fmt(result.new_advance)}</b></div>
            <div>YTD expense trước: <b>{fmt(result.previous_ytd_expense)}</b></div>
            <div>YTD expense sau: <b className="text-emerald-800">{fmt(result.new_ytd_expense)}</b></div>
          </div>
        ) : isInvoiceIn ? (
          <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs font-mono">
            <div>Công nợ trước (331): <b>{fmt(result.previous_balance)}</b></div>
            <div>Giá trị HĐ: <b>{fmt(result.invoice_total)}</b></div>
            <div>
              Công nợ sau (331): <b className="text-emerald-800">{fmt(result.new_balance)}</b>
            </div>
            <div>Trạng thái thanh toán: <b>Chưa trả (UNPAID)</b></div>
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs font-mono">
            <div>Công nợ trước (331): <b>{fmt(result.previous_balance)}</b></div>
            <div>Áp chi: <b>{fmt(result.total_applied)}</b></div>
            <div>
              Chiết khấu:{" "}
              <b>
                {result.discount_amount && result.discount_amount > 0
                  ? fmt(result.discount_amount)
                  : "—"}
              </b>
            </div>
            <div>
              Công nợ sau (331): <b className="text-emerald-800">{fmt(result.new_balance)}</b>
            </div>
          </div>
        )}
        <div className="text-[10px] italic text-emerald-700">
          {isInvoiceIn && "Hoá đơn đã POSTED — chưa ghi nhận đã thanh toán. "}
          Trang tự refresh…
        </div>
      </div>
    );
  }

  if (result?.status === "already_applied") {
    return (
      <div className="rounded-[var(--radius)] border border-blue-200 bg-blue-50 px-4 py-2 text-sm text-blue-800">
        Chứng từ này đã được áp trước đó.
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <Button onClick={submit} disabled={loading || disabled} className="w-full sm:w-auto">
        {loading ? (
          <>
            <Loader2 className="size-4 animate-spin" /> {words.loading}
          </>
        ) : (
          <>
            <CheckCircle2 className="size-4" /> {words.button}
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
