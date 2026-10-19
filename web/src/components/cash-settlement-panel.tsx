"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { CheckCircle2, Loader2, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { selectionMismatchesReference, type ReferenceCheckView } from "@/lib/voucher-view";
import { contractSchedulesOf, type ContractScheduleImpactView } from "@/lib/contract-schedule";
import { ContractScheduleImpact } from "@/components/contract-schedule-impact";
import { PaymentDiscountProposals, type PaymentDiscountProposalView } from "@/components/payment-discount-proposals";

// Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ AR/AP.
// Everything shown here comes from GET /vouchers/{id}/settlement, computed
// server-side (direction from the Company Profile, counterparty by MST,
// deterministic invoice matching). The browser never decides which party
// is MY_COMPANY and never applies money itself — it only sends ADMIN's
// explicit choice to the Gateway, which re-validates everything.

export interface SettlementParty { name: string | null; taxCode: string | null }
export interface SettlementOpenItem {
  id: string; documentNumber: string; documentDate: string; dueDate: string | null;
  originalAmount: number; paidAmount: number; outstanding: number; settlementStatus: string;
}
export interface SettlementAllocationSuggestion {
  invoiceId: string; documentNumber: string; outstanding: number; applyAmount: number; matchMethod: string;
}
export interface SettlementPosting {
  id: string; direction: string; amount: number; transactionDate: string; paymentMethod: string;
  cashAccount: string; counterAccount: string; documentNumber: string; status: string;
  postedAt: string; reversedAt: string | null; reversalReason: string | null;
  appliedAmount: number; unappliedAmount: number; documentReference: string | null;
}
export interface SettlementApplication {
  id: string; cashTransactionId: string; invoiceId: string; invoiceDocumentNumber: string;
  appliedAmount: number; matchMethod: string; status: string; appliedAt: string; reversedAt: string | null;
}
export interface SettlementContext {
  voucher: {
    voucherId: string; voucherType: string; documentNumber: string | null; documentDate: string | null;
    amount: number | null; currency: string; paymentMethodRaw: string | null; paymentMethod: string | null;
    payer: SettlementParty; payee: SettlementParty; description: string | null;
    invoiceReferences: string[]; contractReference: string | null;
  };
  direction: { direction: string; source: string; counterparty: SettlementParty | null; reason: string | null };
  counterparty: { id: string; type: string; code: string; name: string; taxCode: string | null; currentBalance: number; matchedBy: string } | null;
  openItems: SettlementOpenItem[];
  suggestion: { certainty: string; method: string | null; allocations: SettlementAllocationSuggestion[]; unappliedAmount: number; reason: string } | null;
  posting: SettlementPosting | null;
  applications: SettlementApplication[];
  reversedPostings: SettlementPosting[];
  proposedJournal: { debitAccount: string; creditAccount: string; amount: number; description: string }[];
  blockers: string[];
  parties: {
    payer: SettlementParty; payee: SettlementParty;
    companyName: string | null; companyTaxCode: string | null; companyIsPayee: boolean;
  };
  reference: ReferenceCheckView;
  contractSchedules?: ContractScheduleImpactView[];
  discountProposals?: PaymentDiscountProposalView[];
}

const fmt = (n: number | null | undefined) => (n == null ? "—" : Number(n).toLocaleString("vi-VN"));
const fmtDate = (s: string | null | undefined) => {
  if (!s) return "—";
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleDateString("vi-VN");
};

const METHOD_LABEL: Record<string, string> = { cash: "Tiền mặt", bank: "Chuyển khoản" };
const MATCH_LABEL: Record<string, string> = {
  invoice_reference: "Khớp số hoá đơn trên chứng từ",
  exact_amount: "Khớp đúng số tiền còn nợ",
  outstanding_compatible: "Hoá đơn mở duy nhất đủ số dư",
  admin_selected: "ADMIN chọn",
  legacy: "Luồng cũ",
};
const AMBIGUOUS_REASON: Record<string, string> = {
  multiple_open_invoices_admin_selection_required: "Đối tác có nhiều hoá đơn mở và chứng từ không ghi số hoá đơn",
  document_reference_not_open: "Hoá đơn ghi trên chứng từ không còn mở (đã thanh toán hoặc chưa ghi sổ)",
  multiple_invoices_with_exact_amount: "Nhiều hoá đơn có cùng số tiền còn nợ",
  single_open_invoice_covers_amount: "Hoá đơn mở duy nhất đủ số dư",
  amount_missing: "Chứng từ chưa có số tiền",
};
const STATUS_TONE: Record<string, string> = {
  UNPAID: "border-gray-300 bg-gray-50 text-gray-700",
  PARTIAL: "border-amber-300 bg-amber-50 text-amber-800",
  PAID: "border-emerald-300 bg-emerald-50 text-emerald-800",
  OVERDUE: "border-red-300 bg-red-50 text-red-700",
};
const BLOCKER_LABEL: Record<string, string> = {
  customer_not_resolved: "Chưa xác định được khách hàng (MST/tên không khớp danh mục khách hàng của phòng ban).",
  supplier_not_resolved: "Chưa xác định được nhà cung cấp (MST/tên không khớp danh mục NCC của phòng ban).",
  amount_missing: "Chứng từ chưa có số tiền.",
  foreign_currency_not_supported: "Chưa hỗ trợ ngoại tệ (không có nguồn tỷ giá).",
  payment_method_unknown: "Chưa rõ hình thức thanh toán — chọn Tiền mặt / Chuyển khoản bên dưới.",
  voucher_type_unconfirmed: "Loại chứng từ chưa được xác nhận (nhận diện mâu thuẫn hoặc khác loại đã chọn khi tải lên) — ADMIN xác nhận loại ở khung “AIQ nhận diện” phía trên.",
};
const blockerText = (b: string) =>
  b.startsWith("direction_needs_review")
    ? `Không xác định chắc chắn chiều thu/chi (${b.split(":")[1] ?? ""}) — kiểm tra lại bên nộp/bên nhận hoặc Hồ sơ công ty.`
    : BLOCKER_LABEL[b] ?? b;

function StatusPill({ status }: { status: string }) {
  return (
    <span className={`rounded-full border px-2 py-0.5 text-[11px] font-medium ${STATUS_TONE[status] ?? "border-gray-300"}`}>
      {status}
    </span>
  );
}

async function callAction(voucherId: string, action: string, body: unknown) {
  const res = await fetch(`/api/vouchers/${voucherId}/settlement/${action}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {}),
  });
  const json = (await res.json().catch(() => ({}))) as { error?: string; message?: string; outcome?: string };
  if (!res.ok) {
    throw new Error(json.message ?? json.error ?? (res.status === 403 ? "Chỉ ADMIN cùng phòng ban mới được thực hiện." : `HTTP ${res.status}`));
  }
  return json;
}

function AllocationPicker({
  items, initial, available, disabled, onChange,
}: {
  items: SettlementOpenItem[];
  initial: Record<string, number>;
  available: number;
  disabled: boolean;
  onChange: (next: Record<string, number>) => void;
}) {
  const [sel, setSel] = useState<Record<string, number>>(initial);
  const update = (next: Record<string, number>) => { setSel(next); onChange(next); };
  const total = Object.values(sel).reduce((a, b) => a + b, 0);
  if (items.length === 0) {
    return <p className="text-xs text-[var(--color-muted-fg)]">Đối tác không có hoá đơn/công nợ mở.</p>;
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-sm">
        <thead className="text-left text-xs text-[var(--color-muted-fg)]">
          <tr>
            <th className="w-8 px-2 py-1" />
            <th className="px-2 py-1">Hoá đơn</th>
            <th className="px-2 py-1">Hạn TT</th>
            <th className="px-2 py-1 text-right">Giá trị</th>
            <th className="px-2 py-1 text-right">Còn nợ</th>
            <th className="px-2 py-1">TT thanh toán</th>
            <th className="px-2 py-1 text-right">Số tiền đối trừ</th>
          </tr>
        </thead>
        <tbody>
          {items.map((it) => {
            const checked = sel[it.id] !== undefined;
            return (
              <tr key={it.id} className="border-t">
                <td className="px-2 py-1">
                  <input
                    type="checkbox"
                    aria-label={`Chọn ${it.documentNumber}`}
                    checked={checked}
                    disabled={disabled}
                    onChange={(e) => {
                      const next = { ...sel };
                      if (e.target.checked) {
                        next[it.id] = Math.max(0, Math.min(it.outstanding, available - total));
                      } else {
                        delete next[it.id];
                      }
                      update(next);
                    }}
                  />
                </td>
                <td className="px-2 py-1 font-mono">{it.documentNumber}</td>
                <td className="px-2 py-1">{fmtDate(it.dueDate)}</td>
                <td className="px-2 py-1 text-right font-mono">{fmt(it.originalAmount)}</td>
                <td className="px-2 py-1 text-right font-mono">{fmt(it.outstanding)}</td>
                <td className="px-2 py-1"><StatusPill status={it.settlementStatus} /></td>
                <td className="px-2 py-1 text-right">
                  <input
                    type="number"
                    min={0}
                    max={it.outstanding}
                    step={1}
                    disabled={disabled || !checked}
                    value={checked ? sel[it.id] : ""}
                    onChange={(e) => update({ ...sel, [it.id]: Math.max(0, Number(e.target.value) || 0) })}
                    className="h-8 w-40 rounded border px-2 text-right font-mono disabled:opacity-50"
                  />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="mt-2 flex flex-wrap gap-x-6 gap-y-1 text-xs">
        <span>Tổng đối trừ: <b className="font-mono">{fmt(total)}</b></span>
        <span>
          Chưa phân bổ (giữ lại):{" "}
          <b className={`font-mono ${available - total < 0 ? "text-red-700" : ""}`}>{fmt(available - total)}</b>
        </span>
      </div>
    </div>
  );
}

function directionText(context: SettlementContext): string {
  const { source } = context.direction;
  const tax = context.parties?.companyTaxCode;
  const name = context.parties?.companyName;
  if (source === "company_profile_tax_code" || source === "company_profile_tax_code_document") {
    return `Công ty chủ thể được xác định theo MST ${tax ?? ""}${name ? ` (${name})` : ""}`;
  }
  if (source === "company_profile_name") {
    return `Công ty chủ thể được xác định theo tên khớp Hồ sơ công ty${name ? ` (${name})` : ""}`;
  }
  if (source === "voucher_type") {
    return context.direction.reason === "company_profile_missing"
      ? "Theo loại chứng từ — chưa cấu hình Hồ sơ công ty (MST công ty chủ thể)"
      : "Theo loại chứng từ — không tìm thấy MST/tên công ty chủ thể trên chứng từ";
  }
  return "—";
}

function ReferenceNotice({ context, selectedDocs }: { context: SettlementContext; selectedDocs: string[] }) {
  const ref = context.reference;
  if (!ref || ref.documentReferences.length === 0) return null;
  const refText = ref.documentReferences.join(", ");
  const closedRefs = ref.referencedInvoices.filter((r) => r.outstanding <= 0);
  const closedNote = closedRefs.length > 0
    ? ` (hoá đơn ${closedRefs.map((r) => r.documentNumber).join(", ")} đã ${closedRefs[0].settlementStatus} — không còn để đối trừ)`
    : ref.referencedInvoices.length === 0 ? " (không có hoá đơn này trong công nợ của đối tác)" : "";

  if (ref.status === "mismatch") {
    return (
      <div className="rounded-[var(--radius)] border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900">
        <div>Tham chiếu trên chứng từ: <b className="font-mono">{refText}</b>{closedNote}</div>
        <div>Hóa đơn được phân bổ: <b className="font-mono">{ref.appliedInvoices.join(", ")}</b></div>
        <div className="mt-1 font-medium">⚠ Không trùng tham chiếu — ADMIN đã chọn thủ công.</div>
      </div>
    );
  }
  if (ref.status === "matched") {
    return (
      <div className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50/60 px-3 py-2 text-xs text-emerald-900">
        Khớp tham chiếu chứng từ: <b className="font-mono">{refText}</b> = hóa đơn được phân bổ.
      </div>
    );
  }
  // Not posted yet: warn live while ADMIN picks a different invoice.
  const mismatch = selectionMismatchesReference(ref, selectedDocs);
  return (
    <div className={`rounded-[var(--radius)] border px-3 py-2 text-xs ${mismatch ? "border-amber-300 bg-amber-50 text-amber-900" : "border-sky-200 bg-white/60"}`}>
      <div>Tham chiếu trên chứng từ: <b className="font-mono">{refText}</b>{closedNote}</div>
      {mismatch && (
        <>
          <div>Hóa đơn đang chọn: <b className="font-mono">{selectedDocs.join(", ")}</b></div>
          <div className="mt-1 font-medium">⚠ Không trùng tham chiếu — sẽ ghi nhận là ADMIN chọn thủ công.</div>
        </>
      )}
    </div>
  );
}

export function CashSettlementPanel({
  voucherId, context, canApprove,
}: { voucherId: string; context: SettlementContext; canApprove: boolean }) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirmReverse, setConfirmReverse] = useState(false);
  const [method, setMethod] = useState<string>(context.voucher.paymentMethod ?? "");

  const moneyIn = (context.posting?.direction ?? context.direction.direction) === "money_in";
  const posting = context.posting;
  const amount = context.voucher.amount ?? 0;
  const available = posting ? posting.unappliedAmount : amount;
  const certain = context.suggestion?.certainty === "certain";

  const initialSelection = useMemo(() => {
    const init: Record<string, number> = {};
    // Pre-fill only a CERTAIN match; an ambiguous one is shown as a hint
    // below but ADMIN must tick the invoice(s) explicitly.
    if (certain) for (const a of context.suggestion!.allocations) init[a.invoiceId] = a.applyAmount;
    return init;
  }, [certain, context.suggestion]);
  const [selection, setSelection] = useState<Record<string, number>>(initialSelection);

  const run = async (action: string, body: unknown) => {
    setBusy(action);
    setError(null);
    try {
      await callAction(voucherId, action, body);
      setConfirmReverse(false);
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Lỗi mạng");
    } finally {
      setBusy(null);
    }
  };

  const allocations = Object.entries(selection)
    .filter(([, amt]) => amt > 0)
    .map(([invoiceId, amt]) => ({ invoiceId, amount: amt }));
  const selectedTotal = allocations.reduce((a, b) => a + b.amount, 0);
  const blockers = context.blockers.filter((b) => !(b === "payment_method_unknown" && method));
  const cp = context.counterparty;
  const kind = context.voucher.voucherType === "receipt" ? "PHIẾU THU" : context.voucher.voucherType === "bank_transfer" ? "UỶ NHIỆM CHI" : "PHIẾU CHI";
  const journal = posting
    ? [{ debitAccount: moneyIn ? posting.cashAccount : "331", creditAccount: moneyIn ? "131" : posting.cashAccount, amount: posting.amount }]
    : context.proposedJournal;

  return (
    <section className="space-y-4 rounded-[var(--radius)] border border-sky-200 bg-sky-50/30 p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-serif text-lg">{moneyIn ? "Thu tiền · Đối trừ công nợ phải thu (AR)" : "Chi tiền · Đối trừ công nợ phải trả (AP)"}</h2>
        <span className={`rounded-full border px-2 py-0.5 text-xs font-medium ${posting ? "border-emerald-300 bg-emerald-50 text-emerald-800" : "border-amber-300 bg-amber-50 text-amber-800"}`}>
          {posting ? "ĐÃ GHI NHẬN (POSTED)" : "CHƯA GHI NHẬN"}
        </span>
      </div>

      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div><dt className="text-xs text-[var(--color-muted-fg)]">Loại</dt><dd className="font-medium">{kind}</dd></div>
        <div>
          <dt className="text-xs text-[var(--color-muted-fg)]">Đối tác</dt>
          <dd className="font-medium">
            {cp ? cp.name : context.direction.counterparty?.name ?? "—"}
            {cp && <span className="ml-1 font-mono text-xs text-[var(--color-muted-fg)]">({cp.code}{cp.taxCode ? ` · MST ${cp.taxCode}` : ""})</span>}
          </dd>
        </div>
        <div><dt className="text-xs text-[var(--color-muted-fg)]">Số tiền</dt><dd className="font-mono font-medium">{fmt(amount)} {context.voucher.currency}</dd></div>
        <div>
          <dt className="text-xs text-[var(--color-muted-fg)]">Hình thức</dt>
          <dd>
            {posting ? METHOD_LABEL[posting.paymentMethod] ?? posting.paymentMethod
              : context.voucher.paymentMethod ? METHOD_LABEL[context.voucher.paymentMethod]
              : canApprove ? (
                <select value={method} onChange={(e) => setMethod(e.target.value)} className="h-8 rounded border px-2 text-sm">
                  <option value="">— Chọn —</option>
                  <option value="cash">Tiền mặt</option>
                  <option value="bank">Chuyển khoản</option>
                </select>
              ) : "Chưa rõ"}
          </dd>
        </div>
        <div>
          <dt className="text-xs text-[var(--color-muted-fg)]">Công nợ hiện tại {moneyIn ? "(phải thu 131)" : "(phải trả 331)"}</dt>
          <dd className="font-mono font-medium">{cp ? fmt(cp.currentBalance) : "—"}</dd>
        </div>
        <div>
          <dt className="text-xs text-[var(--color-muted-fg)]">Xác định chiều</dt>
          <dd className="text-xs">{directionText(context)}</dd>
        </div>
      </dl>

      {!posting && blockers.length > 0 && (
        <ul className="list-disc space-y-1 rounded-[var(--radius)] border border-amber-200 bg-amber-50 py-2 pl-6 pr-3 text-xs text-amber-900">
          {blockers.map((b) => <li key={b}>{blockerText(b)}</li>)}
        </ul>
      )}

      {context.suggestion && context.suggestion.allocations.length > 0 && available > 0 && (
        <div className={`rounded-[var(--radius)] border px-3 py-2 text-sm ${certain ? "border-emerald-200 bg-emerald-50/60" : "border-amber-200 bg-amber-50/60"}`}>
          <div className="mb-1 text-xs font-medium">
            {certain
              ? <>Đề xuất khớp · {MATCH_LABEL[context.suggestion.method ?? ""] ?? context.suggestion.reason}</>
              : <>Chưa chắc chắn — ADMIN cần chọn hoá đơn · {AMBIGUOUS_REASON[context.suggestion.reason] ?? context.suggestion.reason} (gợi ý theo hạn thanh toán sớm nhất)</>}
          </div>
          {context.suggestion.allocations.map((a) => (
            <div key={a.invoiceId} className="grid grid-cols-1 gap-x-6 font-mono text-xs sm:grid-cols-3">
              <span>Hoá đơn {a.documentNumber}</span>
              <span>Còn nợ: {fmt(a.outstanding)}</span>
              <span>Đối trừ: <b>{fmt(a.applyAmount)}</b></span>
            </div>
          ))}
          {context.suggestion.unappliedAmount > 0 && (
            <div className="mt-1 text-xs">Chưa phân bổ: <b className="font-mono">{fmt(context.suggestion.unappliedAmount)}</b> (giữ là tiền ứng trước / chưa xác định)</div>
          )}
        </div>
      )}
      {context.suggestion?.certainty === "no_open_item" && !posting && (
        <p className="rounded-[var(--radius)] border border-blue-200 bg-blue-50 px-3 py-2 text-xs text-blue-800">
          Đối tác không có hoá đơn/công nợ mở — khi duyệt, toàn bộ số tiền được ghi nhận là <b>CHƯA PHÂN BỔ</b>
          (không giảm công nợ hoá đơn nào). ADMIN có thể phân bổ sau.
        </p>
      )}

      <ReferenceNotice context={context} selectedDocs={posting ? [] : context.openItems.filter((i) => (selection[i.id] ?? 0) > 0).map((i) => i.documentNumber)} />

      {journal.length > 0 && (
        <div className="rounded border bg-[var(--color-bg)] px-3 py-2 font-mono text-xs">
          {journal.map((j, i) => (
            <div key={i}>Nợ {j.debitAccount} / Có {j.creditAccount}: <b>{fmt(j.amount)}</b></div>
          ))}
        </div>
      )}

      {posting && (
        <div className="space-y-2">
          <div className="text-xs text-[var(--color-muted-fg)]">
            Ghi nhận ngày {fmtDate(posting.postedAt)} · Đã đối trừ <b className="font-mono">{fmt(posting.appliedAmount)}</b> ·
            Chưa phân bổ <b className="font-mono">{fmt(posting.unappliedAmount)}</b>
          </div>
          {context.applications.filter((a) => a.cashTransactionId === posting.id).length > 0 && (
            <table className="w-full text-sm">
              <thead className="text-left text-xs text-[var(--color-muted-fg)]">
                <tr><th className="px-2 py-1">Hoá đơn</th><th className="px-2 py-1 text-right">Đối trừ</th><th className="px-2 py-1">Căn cứ</th><th className="px-2 py-1">Trạng thái</th><th /></tr>
              </thead>
              <tbody>
                {context.applications.filter((a) => a.cashTransactionId === posting.id).map((a) => (
                  <tr key={a.id} className="border-t">
                    <td className="px-2 py-1 font-mono">{a.invoiceDocumentNumber}</td>
                    <td className="px-2 py-1 text-right font-mono">{fmt(a.appliedAmount)}</td>
                    <td className="px-2 py-1 text-xs">{MATCH_LABEL[a.matchMethod] ?? a.matchMethod}</td>
                    <td className="px-2 py-1 text-xs">{a.status === "active" ? "Hiệu lực" : `Đã huỷ ${fmtDate(a.reversedAt)}`}</td>
                    <td className="py-1 text-right">
                      {canApprove && a.status === "active" && (
                        <Button variant="ghost" size="sm" disabled={busy !== null}
                          onClick={() => run("unapply", { applicationId: a.id, reason: "Huỷ phân bổ" })}>
                          {busy === "unapply" ? <Loader2 className="size-3.5 animate-spin" /> : <Undo2 className="size-3.5" />} Huỷ phân bổ
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <ContractScheduleImpact schedules={contractSchedulesOf(context)} />
      <PaymentDiscountProposals proposals={context.discountProposals} canApprove={canApprove && !posting}
        confirmUrl={`/api/vouchers/${voucherId}/settlement/discount-confirm`} />

      {canApprove && available > 0 && (!posting ? blockers.length === 0 || blockers.every((b) => b === "payment_method_unknown") : true) && (
        <div className="space-y-2 border-t pt-3">
          <div className="text-sm font-medium">Hạch toán vào hóa đơn/công nợ</div>
          <AllocationPicker
            key={posting?.id ?? "new"}
            items={context.openItems}
            initial={posting ? {} : initialSelection}
            available={available}
            disabled={busy !== null}
            onChange={setSelection}
          />
        </div>
      )}

      {canApprove && !posting && (
        <div className="space-y-2">
          <Button
            disabled={busy !== null || blockers.length > 0 || selectedTotal > available || (!context.voucher.paymentMethod && !method)}
            onClick={() => run("post", {
              allocations: allocations.length > 0 ? allocations : null,
              leaveUnapplied: allocations.length === 0,
              paymentMethod: context.voucher.paymentMethod ? null : method || null,
            })}
          >
            {busy === "post" ? <Loader2 className="size-4 animate-spin" /> : <CheckCircle2 className="size-4" />} Duyệt &amp; ghi nhận
          </Button>
          <p className="text-[11px] italic text-[var(--color-muted-fg)]">
            Một giao dịch: ghi {moneyIn ? "Nợ 111/112 · Có 131" : "Nợ 331 · Có 111/112"}, đối trừ đúng các hoá đơn đã chọn,
            cập nhật công nợ. {allocations.length === 0 ? "Không chọn hoá đơn nào → tiền được giữ CHƯA PHÂN BỔ." : ""} Bấm lại không ghi trùng.
          </p>
        </div>
      )}

      {canApprove && posting && posting.unappliedAmount > 0 && context.openItems.length > 0 && (
        <Button variant="outline" disabled={busy !== null || allocations.length === 0 || selectedTotal > available}
          onClick={() => run("allocate", { allocations })}>
          {busy === "allocate" ? <Loader2 className="size-4 animate-spin" /> : <CheckCircle2 className="size-4" />} Phân bổ vào hoá đơn đã chọn
        </Button>
      )}

      {canApprove && posting && (
        <div className="border-t pt-3">
          {!confirmReverse ? (
            <Button variant="outline" size="sm" disabled={busy !== null} onClick={() => setConfirmReverse(true)}>
              <Undo2 className="size-3.5" /> Huỷ ghi nhận (đảo bút toán)
            </Button>
          ) : (
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span>Đảo toàn bộ bút toán và khôi phục công nợ? Lịch sử được giữ lại.</span>
              <Button variant="destructive" size="sm" disabled={busy !== null} onClick={() => run("reverse", { reason: "ADMIN huỷ ghi nhận" })}>
                {busy === "reverse" ? <Loader2 className="size-3.5 animate-spin" /> : null} Xác nhận đảo
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setConfirmReverse(false)}>Thôi</Button>
            </div>
          )}
        </div>
      )}

      {!canApprove && !posting && (
        <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          Chỉ ADMIN cùng phòng ban mới có thể Duyệt &amp; ghi nhận.
        </p>
      )}

      {context.reversedPostings.length > 0 && (
        <details className="text-xs text-[var(--color-muted-fg)]">
          <summary>Lịch sử ghi nhận đã huỷ ({context.reversedPostings.length})</summary>
          {context.reversedPostings.map((p) => (
            <div key={p.id}>
              {fmtDate(p.postedAt)} → huỷ {fmtDate(p.reversedAt)}: {fmt(p.amount)} {p.reversalReason ? `(${p.reversalReason})` : ""}
            </div>
          ))}
        </details>
      )}

      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">Lỗi: {error}</p>
      )}
    </section>
  );
}
