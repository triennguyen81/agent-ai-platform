"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

// Contract payment-discount PROPOSAL (read-only, computed by the Gateway
// from the Contract's structured term + the posted invoice + the receipt's
// own date/amount). Never applied automatically: "Áp dụng chiết khấu" sends
// only what the ADMIN saw; the Gateway re-evaluates and either refuses a
// stale proposal or applies it — and until an accountant-approved discount
// accounting policy exists it always stops at
// payment_discount_accounting_policy_missing.

export interface PaymentDiscountEvaluationView {
  status: "eligible" | "not_eligible" | "not_applicable";
  reasonCode: string;
  invoiceId: string;
  invoiceDocumentNumber: string;
  contractId?: string | null;
  termId?: string | null;
  termRevision?: number | null;
  ratePercent?: number | null;
  basis?: string | null;
  daysCount?: number | null;
  basisDate?: string | null;
  qualifyingDeadline?: string | null;
  receiptDate?: string | null;
  openAmount: number;
  cashAvailable: number;
  calculatedDiscount: number;
  cashToApply: number;
  remainingCashRequired: number;
  excessCash: number;
}

export interface PaymentDiscountProposalView {
  evaluation: PaymentDiscountEvaluationView;
  contractLabel?: string | null;
  accountingPolicyAvailable: boolean;
  blockedBy?: string | null;
}

const REASONS: Record<string, string> = {
  eligible_full_settlement_within_window: "Đủ điều kiện: thanh toán đủ trong thời hạn chiết khấu.",
  payment_after_deadline: "Không đủ điều kiện: nhận tiền sau hạn chiết khấu.",
  receipt_before_basis_date: "Không đủ điều kiện: ngày nhận tiền trước ngày hoá đơn.",
  receipt_date_missing: "Không đủ điều kiện: chứng từ chưa có ngày nhận tiền.",
  partial_payment_discount_policy_undefined: "Không áp dụng: thanh toán một phần — chưa có chính sách chiết khấu cho trường hợp này.",
  contract_not_effective: "Không áp dụng: hợp đồng chưa được duyệt.",
  contract_not_sales: "Không áp dụng: không phải hợp đồng bán.",
  contract_scope_mismatch: "Không áp dụng: hợp đồng thuộc phòng ban khác.",
  contract_not_found: "Không áp dụng: không tìm thấy hợp đồng liên kết.",
  contract_customer_mismatch: "Không áp dụng: khách hàng của hoá đơn không phải bên mua trong hợp đồng.",
  currency_mismatch: "Không áp dụng: khác loại tiền với điều khoản.",
  discount_basis_unsupported: "Không áp dụng: mốc tính chưa được hỗ trợ.",
  discount_rounds_to_zero: "Không áp dụng: số chiết khấu làm tròn bằng 0.",
};

const ERRORS: Record<string, string> = {
  payment_discount_accounting_policy_missing:
    "Chưa có chính sách hạch toán chiết khấu thanh toán được kế toán phê duyệt — chưa thể áp dụng chiết khấu. Có thể ghi nhận khoản thu không kèm chiết khấu.",
  discount_terms_changed: "Điều khoản chiết khấu đã thay đổi — tải lại trang.",
  discount_eligibility_changed: "Điều kiện chiết khấu đã thay đổi — tải lại trang.",
  invoice_balance_changed: "Công nợ hoá đơn đã thay đổi — tải lại trang.",
  document_review_required: "Chứng từ đang chờ duyệt / bị chặn — chưa thể ghi nhận.",
};

const vnd = (n: number) => `${new Intl.NumberFormat("vi-VN").format(n)} ₫`;
const day = (d?: string | null) => (d ? new Date(`${d}T00:00:00`).toLocaleDateString("vi-VN") : "—");

export function PaymentDiscountProposals({
  proposals, confirmUrl, canApprove,
}: { proposals?: PaymentDiscountProposalView[] | null; confirmUrl: string; canApprove: boolean }) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  if (!proposals || proposals.length === 0) return null;

  async function confirm(e: PaymentDiscountEvaluationView) {
    setBusy(e.invoiceId);
    setMessage(null);
    try {
      const res = await fetch(confirmUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          invoiceId: e.invoiceId, termId: e.termId, termRevision: e.termRevision,
          discountAmount: e.calculatedDiscount, openAmount: e.openAmount,
        }),
      });
      const body = (await res.json().catch(() => ({}))) as { error?: string; message?: string };
      if (res.ok) { router.refresh(); return; }
      setMessage(ERRORS[body.error ?? ""] ?? body.message ?? "Không thể áp dụng chiết khấu.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="rounded-md border border-sky-200 bg-sky-50 p-3 text-sm" data-testid="payment-discount-proposals">
      <h3 className="font-medium">Chiết khấu thanh toán theo hợp đồng (đề xuất)</h3>
      <p className="text-xs text-[var(--color-muted-fg)]">
        Tính tất định từ điều khoản hợp đồng đã duyệt, hoá đơn đã ghi sổ và ngày/số tiền nhận — không tự áp dụng.
      </p>
      {proposals.map(({ evaluation: e, contractLabel, blockedBy }) => (
        <div key={e.invoiceId} className="mt-2 rounded border bg-white p-2" data-testid="payment-discount-proposal">
          <div className="grid grid-cols-2 gap-x-4 gap-y-0.5 sm:grid-cols-3">
            <span>Hoá đơn: <b>{e.invoiceDocumentNumber}</b></span>
            <span>Công nợ mở: {vnd(e.openAmount)}</span>
            <span>Tiền nhận: {vnd(e.cashAvailable)}</span>
            <span>Hợp đồng: {contractLabel ?? "—"}</span>
            <span>Điều khoản: {e.ratePercent}% trong {e.daysCount} ngày (từ ngày hoá đơn) · v{e.termRevision}</span>
            <span>Hạn chiết khấu: {day(e.qualifyingDeadline)} · Ngày nhận: {day(e.receiptDate)}</span>
            {e.status === "eligible" && (
              <>
                <span>Chiết khấu tính được: <b>{vnd(e.calculatedDiscount)}</b></span>
                <span>Tiền ghi nhận cho hoá đơn: {vnd(e.cashToApply)}</span>
                {e.excessCash > 0 && <span>Tiền thừa (để chưa phân bổ): {vnd(e.excessCash)}</span>}
              </>
            )}
          </div>
          <p className={e.status === "eligible" ? "mt-1 text-emerald-700" : "mt-1 text-amber-800"} data-testid="payment-discount-reason">
            {REASONS[e.reasonCode] ?? e.reasonCode}
          </p>
          {e.status === "eligible" && blockedBy && (
            <p className="mt-1 text-red-700" data-testid="payment-discount-blocked">{ERRORS[blockedBy] ?? blockedBy}</p>
          )}
          {e.status === "eligible" && canApprove && (
            <div className="mt-2 flex flex-wrap gap-2">
              <button
                type="button"
                className="rounded border border-sky-600 px-2 py-1 text-sky-700 disabled:opacity-50"
                disabled={busy !== null}
                onClick={() => confirm(e)}
              >
                {busy === e.invoiceId ? "Đang kiểm tra…" : "Áp dụng chiết khấu và ghi nhận"}
              </button>
              <span className="self-center text-xs text-[var(--color-muted-fg)]">
                Không áp dụng chiết khấu: dùng “Duyệt &amp; ghi nhận” như bình thường.
              </span>
            </div>
          )}
        </div>
      ))}
      {message && <p className="mt-2 text-red-700" data-testid="payment-discount-error">{message}</p>}
    </section>
  );
}
