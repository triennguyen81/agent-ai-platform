"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Banknote, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { TAX_ERROR_LABEL } from "@/lib/tax-verification";
import {
  APPROVAL_LABEL,
  PAYMENT_LABEL,
  RECOGNITION_LABEL,
  maskAccount,
  payBody,
  sourceBlocker,
  type ExpenseClaimPaymentView,
  type PaymentChoice,
} from "@/lib/expense-claim-payment";

// PHÊ DUYỆT / XÁC MINH THUẾ / THANH TOÁN for an expense claim. "Duyệt & chi
// tiền" is the final approval AND the actual payment (canonical cash posting +
// journal) in one Gateway transaction. The browser only picks the source.

const fmt = (n: number | null | undefined) => (n == null ? "—" : Number(n).toLocaleString("vi-VN"));
const fmtDate = (s: string | null | undefined) => (s ? new Date(s).toLocaleString("vi-VN") : "—");

export function ExpenseClaimPaymentPanel({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [view, setView] = useState<ExpenseClaimPaymentView | null>(null);
  const [canPay, setCanPay] = useState(false);
  const [choice, setChoice] = useState<PaymentChoice | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    const res = await fetch(`/api/vouchers/${voucherId}/expense-claim/payment`, { cache: "no-store" });
    if (!res.ok) return;
    const json = (await res.json()) as { context: ExpenseClaimPaymentView; canPay: boolean };
    setView(json.context);
    setCanPay(json.canPay);
  }, [voucherId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function pay() {
    if (!choice) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/expense-claim/pay`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payBody(choice)),
      });
      const json = (await res.json().catch(() => ({}))) as { error?: string; message?: string; outcome?: string };
      if (!res.ok) {
        setError((json.error && TAX_ERROR_LABEL[json.error]) ?? json.message ?? json.error ?? `HTTP ${res.status}`);
        await load();
        return;
      }
      if (json.outcome === "already_paid") setNotice("Đề nghị đã được thanh toán trước đó — không chi lại.");
      await load();
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  if (!view) return null;
  const paid = view.paymentStatus === "paid";
  const p = view.payment;
  const banks = view.companyBankAccounts;
  const hint = sourceBlocker(choice);
  const approvalKey = view.reviewStatus === "accounting_reviewed" && !paid ? "accounting_reviewed" : view.reviewStatus;

  return (
    <section className="space-y-3 rounded-[var(--radius)] border p-4 text-sm" data-testid="expense-payment-panel" data-payment-status={view.paymentStatus}>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt className="font-medium">PHÊ DUYỆT</dt>
        <dd data-testid="approval-status">
          {APPROVAL_LABEL[approvalKey] ?? approvalKey}
          {view.reviewStatus === "accounting_reviewed" && !paid && <span className="text-[var(--color-muted-fg)]"> (bộ phận + kế toán) — chờ duyệt chi</span>}
        </dd>
        <dt className="font-medium">XÁC MINH THUẾ</dt>
        <dd>{!view.taxVerificationRequired ? "Không yêu cầu" : view.taxGate ? "Chưa xác minh" : "Đã xác minh"}</dd>
        <dt className="font-medium">GHI NHẬN KẾ TOÁN</dt>
        <dd data-testid="recognition-status" className={view.recognition.status === "recognized" ? "font-semibold text-emerald-700" : ""}>
          {RECOGNITION_LABEL[view.recognition.status] ?? view.recognition.status}
        </dd>
        <dt className="font-medium">THANH TOÁN</dt>
        <dd data-testid="payment-status" className={paid ? "font-semibold text-emerald-700" : ""}>
          {paid ? "ĐÃ CHI" : PAYMENT_LABEL[view.paymentStatus] ?? view.paymentStatus}
        </dd>
        <dt className="text-[var(--color-muted-fg)]">Nhân viên nhận</dt>
        <dd>{view.employee ? `${view.employee.name} (${view.employee.code})` : "—"}</dd>
        <dt className="text-[var(--color-muted-fg)]">Số tiền chi</dt>
        <dd className="font-mono" data-testid="payable-amount">{fmt(view.payableAmount)} {view.currency}</dd>
      </dl>

      <div className="rounded border px-3 py-2 text-xs" data-testid="recognition-section" data-preview={String(view.recognition.isPreview)}>
        <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-0.5">
          <dt>Chi phí ghi nhận</dt><dd className="font-mono">{fmt(view.recognition.expenseAmount)}</dd>
          <dt>Thuế GTGT được khấu trừ</dt><dd className="font-mono">{fmt(view.recognition.vatDeductible)}</dd>
          <dt>Tạm ứng đã cấn</dt><dd className="font-mono" data-testid="advance-cleared">{fmt(view.recognition.advanceCleared)}</dd>
          <dt>Phải trả nhân viên</dt><dd className="font-mono" data-testid="employee-payable">{fmt(view.recognition.employeePayable)}</dd>
          {!view.recognition.isPreview && (
            <>
              <dt>Người ghi nhận</dt><dd>{view.recognition.recognizedByName ?? view.recognition.recognizedBy ?? "—"}</dd>
              <dt>Thời gian</dt><dd>{fmtDate(view.recognition.recognizedAt)}</dd>
            </>
          )}
        </dl>
        {view.recognition.lines.length > 0 && (
          <div className="mt-1 font-mono" data-testid="recognition-journal">
            <div className="font-sans font-medium">
              {view.recognition.isPreview ? "Bút toán sẽ ghi nhận khi “Duyệt kế toán” (chưa ghi sổ)" : "Bút toán đã ghi nhận"}
            </div>
            {view.recognition.lines.map((j, i) => <div key={i}>Nợ {j.debitAccount} / Có {j.creditAccount}: {fmt(j.amount)}</div>)}
          </div>
        )}
        {view.recognition.isPreview && view.recognition.blockers.length > 0 && view.reviewStatus === "dept_reviewed" && (
          <ul className="mt-1 text-amber-800" data-testid="recognition-blockers">
            {view.recognition.blockers.map((b) => <li key={b.code} data-code={b.code}>{b.message}</li>)}
          </ul>
        )}
      </div>

      {paid && p && (
        <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-0.5 rounded border bg-[var(--color-bg)] px-3 py-2 text-xs" data-testid="payment-details">
          <dt>Nguồn</dt>
          <dd data-testid="payment-source">{p.paymentMethod === "bank" ? maskAccount(p.bankName, p.bankAccountNumber) : `Tiền mặt (${p.cashAccount})`}</dd>
          <dt>Số tiền</dt><dd className="font-mono">{fmt(p.amount)}</dd>
          <dt>Người thực hiện</dt><dd data-testid="paid-by">{p.paidByName ?? p.paidBy ?? "—"}</dd>
          <dt>Thời gian</dt><dd>{fmtDate(p.postedAt)}</dd>
          <dt>Chứng từ chi</dt><dd className="font-mono">{p.documentNumber}</dd>
          <dt>Bút toán chi</dt>
          <dd className="font-mono" data-testid="payment-journal">
            {p.journal.map((j, i) => <div key={i}>Nợ {j.debitAccount} / Có {j.creditAccount}: {fmt(j.amount)}</div>)}
          </dd>
        </dl>
      )}

      {view.paymentStatus === "not_due" && (
        <p className="text-xs text-[var(--color-muted-fg)]">Không phát sinh khoản chi cho nhân viên (tạm ứng đã bù trừ).</p>
      )}

      {view.paymentStatus === "unpaid" && view.blockers.length > 0 && (
        <ul className="space-y-0.5 rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800" data-testid="payment-blockers">
          {view.blockers.map((b) => <li key={b.code} data-code={b.code}>{b.message}</li>)}
        </ul>
      )}

      {view.paymentStatus === "unpaid" && canPay && (
        <div className="space-y-2 border-t pt-3" data-testid="payment-form">
          <div className="text-xs font-medium">Nguồn chi</div>
          <div className="flex flex-wrap gap-3 text-xs">
            <label className="flex items-center gap-1">
              <input type="radio" name="pay-source" aria-label="pay-cash" checked={choice?.method === "cash"} onChange={() => setChoice({ method: "cash" })} />
              Tiền mặt ({view.cashAccount})
            </label>
            <label className="flex items-center gap-1">
              <input type="radio" name="pay-source" aria-label="pay-bank" checked={choice?.method === "bank"}
                onChange={() => setChoice({ method: "bank", companyBankAccountId: banks.length === 1 ? banks[0].id : null })} />
              Tài khoản ngân hàng
            </label>
          </div>
          {choice?.method === "bank" && (
            <label className="block text-xs">
              Tài khoản công ty
              <select aria-label="pay-bank-account" className="mt-1 block w-full rounded border px-2 py-1"
                value={choice.companyBankAccountId ?? ""} onChange={(e) => setChoice({ method: "bank", companyBankAccountId: e.target.value || null })}>
                <option value="">— Chọn tài khoản —</option>
                {banks.map((b) => (
                  <option key={b.id} value={b.id}>{maskAccount(b.bankName, b.accountNumber)} · TK {b.glAccountCode}</option>
                ))}
              </select>
            </label>
          )}
          {choice && !hint && (
            <div className="font-mono text-xs text-[var(--color-muted-fg)]">
              Bút toán: Nợ 334 / Có {choice.method === "cash" ? view.cashAccount : banks.find((b) => b.id === (choice as { companyBankAccountId: string | null }).companyBankAccountId)?.glAccountCode}: {fmt(view.payableAmount)}
            </div>
          )}
          {hint && view.blockers.length === 0 && <p className="text-xs text-amber-800" data-testid="source-hint">{hint.message}</p>}
          <Button size="sm" disabled={busy || view.blockers.length > 0 || hint !== null} onClick={pay}>
            {busy ? <Loader2 className="size-3.5 animate-spin" /> : <Banknote className="size-3.5" />} Duyệt & chi tiền
          </Button>
        </div>
      )}

      {error && <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-800" data-testid="payment-error">{error}</div>}
      {notice && <div className="rounded border border-sky-200 bg-sky-50 px-3 py-2 text-xs text-sky-800" data-testid="payment-notice">{notice}</div>}
    </section>
  );
}
