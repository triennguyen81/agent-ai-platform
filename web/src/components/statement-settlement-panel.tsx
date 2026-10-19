"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { CheckCircle2, Loader2, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  ACCOUNTING_LABEL,
  ALLOCATION_LABEL,
  BLOCKER_LABEL,
  INVOICE_MATCH_LABEL,
  allocationTotals,
  blockingReasons,
  initialAllocation,
  type OpenItemView,
  type StatementSettlementView,
} from "@/lib/statement-settlement";
import { MATCH_METHOD_LABEL } from "@/lib/bank-statement";
import { contractSchedulesOf } from "@/lib/contract-schedule";
import { PaymentDiscountProposals, type PaymentDiscountProposalView } from "@/components/payment-discount-proposals";
import { ContractScheduleImpact } from "@/components/contract-schedule-impact";

// Bank statement money_in line -> canonical cash settlement (same engine as
// Phiếu thu: Nợ 112x / Có 131, payment applications, AR recomputed). The
// browser only sends ADMIN's choice; the Gateway re-validates everything.

const fmt = (n: number | null | undefined) => (n == null ? "—" : Number(n).toLocaleString("vi-VN"));
const fmtDate = (s: string | null | undefined) => {
  if (!s) return "—";
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleDateString("vi-VN");
};
const mask = (acct: string) => (acct.length > 4 ? `xxx${acct.slice(-4)}` : acct);

const STATUS_TONE: Record<string, string> = {
  NOT_POSTED: "border-amber-300 bg-amber-50 text-amber-800",
  POSTED: "border-emerald-300 bg-emerald-50 text-emerald-800",
  RECONCILED: "border-sky-300 bg-sky-50 text-sky-800",
  REVERSED: "border-gray-300 bg-gray-50 text-gray-700",
};

function AllocationTable({
  items, selection, setSelection, available, disabled,
}: {
  items: OpenItemView[];
  selection: Record<string, number>;
  setSelection: (s: Record<string, number>) => void;
  available: number;
  disabled: boolean;
}) {
  const totals = allocationTotals(available, selection);
  if (items.length === 0) return <p className="text-xs text-[var(--color-muted-fg)]">Khách hàng không có hoá đơn còn nợ.</p>;
  return (
    <div className="space-y-1">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[760px] text-xs">
          <thead className="text-left text-[var(--color-muted-fg)]">
            <tr>
              <th className="w-6 px-1 py-1" /><th className="px-1 py-1">Hoá đơn</th><th className="px-1 py-1">Ngày HĐ</th><th className="px-1 py-1">Hạn TT</th>
              <th className="px-1 py-1 text-right">Giá trị</th><th className="px-1 py-1 text-right">Đã thu</th><th className="px-1 py-1 text-right">Còn nợ</th>
              <th className="px-1 py-1">Trạng thái</th><th className="px-1 py-1 text-right">Đối trừ</th><th className="px-1 py-1 text-right">Còn lại</th>
            </tr>
          </thead>
          <tbody>
            {items.map((it) => {
              const checked = selection[it.id] !== undefined;
              const apply = selection[it.id] ?? 0;
              return (
                <tr key={it.id} className="border-t">
                  <td className="px-1 py-1">
                    <input type="checkbox" aria-label={`Chọn ${it.documentNumber}`} checked={checked} disabled={disabled}
                      onChange={(e) => {
                        const next = { ...selection };
                        if (e.target.checked) next[it.id] = Math.max(0, Math.min(it.outstanding, totals.unallocated));
                        else delete next[it.id];
                        setSelection(next);
                      }} />
                  </td>
                  <td className="px-1 py-1 font-mono">{it.documentNumber}</td>
                  <td className="px-1 py-1">{fmtDate(it.documentDate)}</td>
                  <td className="px-1 py-1">{fmtDate(it.dueDate)}</td>
                  <td className="px-1 py-1 text-right font-mono">{fmt(it.originalAmount)}</td>
                  <td className="px-1 py-1 text-right font-mono">{fmt(it.paidAmount)}</td>
                  <td className="px-1 py-1 text-right font-mono">{fmt(it.outstanding)}</td>
                  <td className="px-1 py-1">{it.settlementStatus}</td>
                  <td className="px-1 py-1 text-right">
                    <input type="number" aria-label={`Đối trừ ${it.documentNumber}`} min={0} max={it.outstanding} disabled={disabled || !checked}
                      value={checked ? apply : ""} onChange={(e) => setSelection({ ...selection, [it.id]: Math.max(0, Number(e.target.value) || 0) })}
                      className="h-7 w-32 rounded border px-1 text-right font-mono disabled:opacity-50" />
                  </td>
                  <td className="px-1 py-1 text-right font-mono">{checked ? fmt(it.outstanding - apply) : ""}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="flex flex-wrap gap-x-6 text-xs">
        <span>Tiền nhận: <b className="font-mono">{fmt(available)}</b></span>
        <span>Đã phân bổ: <b className="font-mono">{fmt(totals.allocated)}</b></span>
        <span>Chưa phân bổ: <b className={`font-mono ${totals.overAllocated ? "text-red-700" : ""}`}>{fmt(totals.unallocated)}</b></span>
      </div>
    </div>
  );
}

export function StatementSettlementPanel({ voucherId, lineId, canApprove }: { voucherId: string; lineId: string; canApprove: boolean }) {
  const router = useRouter();
  const [view, setView] = useState<StatementSettlementView | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selection, setSelection] = useState<Record<string, number>>({});
  const [confirmNotDuplicate, setConfirmNotDuplicate] = useState(false);
  const [leaveUnapplied, setLeaveUnapplied] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  const [confirmReverse, setConfirmReverse] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const base = `/api/vouchers/${voucherId}/bank-transactions/${lineId}/settlement`;

  const load = useCallback(async () => {
    const res = await fetch(base, { cache: "no-store" });
    if (!res.ok) { setLoadError(`HTTP ${res.status}`); return; }
    const json = (await res.json()) as { context: StatementSettlementView };
    setView(json.context);
    setSelection(initialAllocation(json.context));
    setAdjusting(false);
    setConfirmReverse(false);
  }, [base]);

  useEffect(() => { void load(); }, [load]);

  const act = async (action: string, body: unknown) => {
    setBusy(action);
    setError(null);
    try {
      const res = await fetch(`${base}/${action}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      const json = (await res.json().catch(() => ({}))) as { message?: string; error?: string };
      if (!res.ok) throw new Error(res.status === 403 ? "Chỉ ADMIN cùng phòng ban mới được thực hiện." : json.message ?? json.error ?? `HTTP ${res.status}`);
      await load();
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Lỗi mạng");
    } finally {
      setBusy(null);
    }
  };

  if (loadError) return <p className="text-xs text-red-700">Không tải được thông tin ghi nhận ({loadError}).</p>;
  if (!view) return <p className="text-xs text-[var(--color-muted-fg)]"><Loader2 className="mr-1 inline size-3 animate-spin" />Đang tải…</p>;

  const v = view;
  const posting = v.posting;
  const allocations = Object.entries(selection).filter(([, a]) => a > 0).map(([invoiceId, amount]) => ({ invoiceId, amount }));
  const available = posting ? posting.unappliedAmount : v.line.amount;
  const totals = allocationTotals(available, selection);
  const blocking = blockingReasons(v, confirmNotDuplicate);
  const canPost = canApprove && !posting && !v.reconciledWith && blocking.length === 0 && !totals.overAllocated
    && (allocations.length > 0 || leaveUnapplied);

  return (
    <div className="space-y-2 rounded border border-sky-200 bg-white/70 p-3 text-xs">
      <div className="flex flex-wrap items-center gap-2">
        <b className="text-sm">Ghi nhận công nợ (AR)</b>
        <span className={`rounded-full border px-2 py-0.5 text-[11px] font-medium ${STATUS_TONE[v.accountingStatus] ?? ""}`}>{ACCOUNTING_LABEL[v.accountingStatus] ?? v.accountingStatus}</span>
        {v.allocationStatus && <span className="text-[var(--color-muted-fg)]">{ALLOCATION_LABEL[v.allocationStatus] ?? v.allocationStatus}</span>}
      </div>

      <div className="grid gap-x-6 gap-y-1 sm:grid-cols-2">
        <div>Khách hàng: <b>{v.customer?.name ?? "Chưa xác định"}</b>{v.customer && <> · Công nợ hiện tại <span className="font-mono">{fmt(v.customer.currentBalance)}</span></>}</div>
        <div>Ngân hàng: {v.companyBankAccount ? <b>{v.companyBankAccount.bankName} {mask(v.companyBankAccount.accountNumber)} <span className="font-normal">(TK {v.companyBankAccount.glAccountCode})</span></b> : <span className="text-amber-800">chưa liên kết</span>}</div>
        <div>Số tiền: <b className="font-mono">{fmt(v.line.amount)}</b></div>
        <div>Mã GD ngân hàng: <span className="font-mono">{v.line.bankReference ?? "—"}</span></div>
      </div>

      {/* ---- posted ---- */}
      {posting && (
        <div className="space-y-2">
          <div className="rounded border bg-[var(--color-bg)] px-2 py-1 font-mono">
            <div>Nợ {posting.cashAccount} &nbsp; {fmt(posting.amount)}</div>
            <div>Có {posting.counterAccount} &nbsp;&nbsp; {fmt(posting.amount)}</div>
          </div>
          <div className="text-[var(--color-muted-fg)]">Ghi nhận {fmtDate(posting.postedAt)} · Đã phân bổ {fmt(posting.appliedAmount)} · Chưa phân bổ {fmt(posting.unappliedAmount)}</div>
          {v.applications.filter((a) => a.cashTransactionId === posting.id).length > 0 && (
            <table className="w-full">
              <thead className="text-left text-[var(--color-muted-fg)]"><tr><th className="px-1">Hoá đơn</th><th className="px-1 text-right">Đối trừ</th><th className="px-1 text-right">Còn nợ</th><th className="px-1">TT hoá đơn</th><th className="px-1">Căn cứ</th><th className="px-1">Phân bổ</th><th /></tr></thead>
              <tbody>
                {v.applications.filter((a) => a.cashTransactionId === posting.id).map((a) => {
                  const inv = v.appliedInvoices.find((i) => i.invoiceId === a.invoiceId);
                  return (
                    <tr key={a.id} className="border-t">
                      <td className="px-1 font-mono">{a.invoiceDocumentNumber}</td>
                      <td className="px-1 text-right font-mono">{fmt(a.appliedAmount)}</td>
                      <td className="px-1 text-right font-mono">{fmt(inv?.outstanding)}</td>
                      <td className="px-1">{inv?.settlementStatus ?? "—"}</td>
                      <td className="px-1">{MATCH_METHOD_LABEL[a.matchMethod] ?? a.matchMethod}</td>
                      <td className="px-1">{a.status === "active" ? "Hiệu lực" : `Đã hoàn tác ${fmtDate(a.reversedAt)}`}</td>
                      <td className="px-1 text-right">
                        {canApprove && a.status === "active" && (
                          <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => act("unapply", { applicationId: a.id, reason: "ADMIN hoàn tác phân bổ" })}>
                            <Undo2 className="size-3" /> Hoàn tác phân bổ
                          </Button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
          <ContractScheduleImpact schedules={contractSchedulesOf(v)} />
          {/* Same Gateway calculator + confirmation core as a Phiếu thu. */}
          <PaymentDiscountProposals proposals={(v as { discountProposals?: PaymentDiscountProposalView[] }).discountProposals}
            canApprove={canApprove && !posting && !v.reconciledWith}
            confirmUrl={`/api/vouchers/${voucherId}/bank-transactions/${lineId}/settlement/discount-confirm`} />
          {canApprove && (
            <div className="flex flex-wrap gap-2">
              {posting.unappliedAmount > 0 && !adjusting && (
                <Button size="sm" variant="outline" onClick={() => { setSelection({}); setAdjusting(true); }}>Điều chỉnh phân bổ</Button>
              )}
              {!confirmReverse ? (
                <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => setConfirmReverse(true)}><Undo2 className="size-3" /> Đảo giao dịch</Button>
              ) : (
                <span className="flex items-center gap-2">
                  Đảo toàn bộ bút toán và khôi phục công nợ? Dòng sao kê vẫn được giữ.
                  <Button size="sm" variant="destructive" disabled={busy !== null} onClick={() => act("reverse", { reason: "ADMIN đảo giao dịch sao kê" })}>
                    {busy === "reverse" ? <Loader2 className="size-3 animate-spin" /> : null} Xác nhận đảo
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => setConfirmReverse(false)}>Thôi</Button>
                </span>
              )}
            </div>
          )}
          {adjusting && (
            <div className="space-y-2 border-t pt-2">
              <AllocationTable items={v.openItems} selection={selection} setSelection={setSelection} available={available} disabled={busy !== null} />
              <Button size="sm" disabled={busy !== null || allocations.length === 0 || totals.overAllocated} onClick={() => act("allocate", { allocations })}>
                {busy === "allocate" ? <Loader2 className="size-3 animate-spin" /> : null} Phân bổ
              </Button>
            </div>
          )}
        </div>
      )}

      {/* ---- reconciled with another source ---- */}
      {v.reconciledWith && (
        <div className="rounded border border-sky-200 bg-sky-50 px-2 py-1">
          Đã đối chiếu với bút toán đã ghi nhận: <b className="font-mono">{v.reconciledWith.documentNumber}</b> ({v.reconciledWith.sourceType === "voucher" ? "Phiếu thu" : "dòng sao kê khác"}),{" "}
          {fmt(v.reconciledWith.amount)} ngày {fmtDate(v.reconciledWith.transactionDate)}
          {v.reconciledWith.voucherId && <> · <Link className="underline" href={`/vouchers/${v.reconciledWith.voucherId}`}>mở chứng từ</Link></>}
        </div>
      )}

      {/* ---- not posted (or reversed: corrected repost) ---- */}
      {!posting && !v.reconciledWith && (
        <div className="space-y-2">
          {v.reversedPostings.length > 0 && <p className="text-[var(--color-muted-fg)]">Đã đảo {v.reversedPostings.length} lần ghi nhận trước — có thể ghi nhận lại.</p>}
          {v.blockers.length > 0 && (
            <ul className="list-disc rounded border border-amber-200 bg-amber-50 py-1 pl-5 pr-2 text-amber-900">
              {v.blockers.map((b) => <li key={b}>{BLOCKER_LABEL[b] ?? b}</li>)}
            </ul>
          )}

          {v.duplicates.length > 0 && (
            <div className="space-y-1 rounded border border-red-200 bg-red-50 px-2 py-1 text-red-900">
              <b>{v.duplicates.some((d) => d.kind === "exact") ? "Đã ghi nhận kế toán" : "Có giao dịch đã ghi nhận có thể trùng"} — NEEDS_RECONCILIATION</b>
              {v.duplicates.map((d) => (
                <div key={d.cashTransactionId} className="flex flex-wrap items-center gap-2">
                  <span>{d.kind === "exact" ? "Trùng chắc chắn" : "Có thể trùng"}: <span className="font-mono">{d.documentNumber}</span> ({d.sourceType === "voucher" ? "Phiếu thu" : "sao kê"}) · {fmt(d.amount)} · {fmtDate(d.transactionDate)}{d.documentReference ? ` · ${d.documentReference}` : ""} — {d.reason}</span>
                  <Link className="underline" href={`/vouchers/${d.voucherId}`}>mở</Link>
                  {canApprove && (
                    <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => act("reconcile", { cashTransactionId: d.cashTransactionId })}>
                      Đối chiếu với giao dịch này
                    </Button>
                  )}
                </div>
              ))}
              {canApprove && !v.duplicates.some((d) => d.kind === "exact") && (
                <label className="flex items-center gap-1"><input type="checkbox" checked={confirmNotDuplicate} onChange={(e) => setConfirmNotDuplicate(e.target.checked)} /> Tôi đã kiểm tra — đây KHÔNG phải giao dịch trùng</label>
              )}
            </div>
          )}

          {v.customer && v.line.direction === "money_in" && (
            <>
              <div className={`rounded border px-2 py-1 ${v.invoiceMatch.status === "AUTO_MATCHED" ? "border-emerald-200 bg-emerald-50/60" : "border-amber-200 bg-amber-50/60"}`}>
                <b>{INVOICE_MATCH_LABEL[v.invoiceMatch.status] ?? v.invoiceMatch.status}</b>
                {v.invoiceMatch.status === "AUTO_MATCHED" && v.invoiceMatch.suggestion?.allocations.map((a) => (
                  <div key={a.invoiceId} className="font-mono">Hoá đơn {a.documentNumber} · Còn nợ {fmt(a.outstanding)} · Đối trừ {fmt(a.applyAmount)} · Còn {fmt(a.outstanding - a.applyAmount)}</div>
                ))}
                {v.invoiceMatch.message && <div className="text-amber-900">⚠ {v.invoiceMatch.message}</div>}
                {v.invoiceMatch.referenceProblems.map((p) => (
                  <div key={p.reference} className="text-amber-900">
                    {p.reference}: {p.problem === "not_booked" ? "chưa có trong sổ công nợ" : p.problem === "other_customer" ? `thuộc khách hàng khác (${p.ownerName})` : `không còn nợ (${p.status})`}
                  </div>
                ))}
              </div>
              <div className="font-medium">Hạch toán vào hoá đơn/công nợ</div>
              <AllocationTable items={v.openItems} selection={selection} setSelection={setSelection} available={available} disabled={!canApprove || busy !== null} />
              {totals.unallocated > 0 && allocations.length > 0 && (
                <p className="text-[var(--color-muted-fg)]">Phần chưa phân bổ {fmt(totals.unallocated)} được giữ là tiền khách trả trước (chưa đối trừ hoá đơn nào).</p>
              )}
              {canApprove && allocations.length === 0 && (
                <label className="flex items-center gap-1"><input type="checkbox" checked={leaveUnapplied} onChange={(e) => setLeaveUnapplied(e.target.checked)} /> Ghi nhận toàn bộ là tiền chưa phân bổ (khách trả trước)</label>
              )}
            </>
          )}

          {v.proposedJournal.length > 0 && (
            <div className="rounded border bg-[var(--color-bg)] px-2 py-1 font-mono">
              {v.proposedJournal.map((j, i) => <div key={i}>Nợ {j.debitAccount} / Có {j.creditAccount}: {fmt(j.amount)}</div>)}
            </div>
          )}

          {canApprove && v.line.direction === "money_in" && (
            <Button disabled={!canPost || busy !== null}
              onClick={() => act("post", { allocations: allocations.length > 0 ? allocations : null, leaveUnapplied: allocations.length === 0 && leaveUnapplied, confirmNotDuplicate })}>
              {busy === "post" ? <Loader2 className="size-4 animate-spin" /> : <CheckCircle2 className="size-4" />} Duyệt &amp; ghi nhận
            </Button>
          )}
          {!canApprove && <p className="text-amber-800">Chỉ ADMIN cùng phòng ban mới có thể Duyệt &amp; ghi nhận.</p>}
        </div>
      )}

      {error && <p className="rounded border border-red-200 bg-red-50 px-2 py-1 text-red-700">Lỗi: {error}</p>}
    </div>
  );
}
