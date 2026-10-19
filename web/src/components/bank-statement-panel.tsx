"use client";

import { Fragment, useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { StatementSettlementPanel } from "@/components/statement-settlement-panel";
import { ACCOUNTING_LABEL } from "@/lib/statement-settlement";
import {
  MATCH_METHOD_LABEL,
  parseMatchEvidence,
  type StatementTransactionView,
} from "@/lib/bank-statement";

// Sao kê ngân hàng: each transaction line -> Customer (money in) / Supplier
// candidate (money out). IDENTIFICATION ONLY — no receipt/payment, journal
// or AR/AP change happens here.

export interface PartyOption { id: string; code: string; name: string }
export interface CompanyAccountOption { id: string; accountNumber: string; bankName: string; glAccountCode: string }

const ACCOUNTING_TONE: Record<string, string> = {
  NOT_POSTED: "text-[var(--color-muted-fg)]",
  POSTED: "text-emerald-800 font-medium",
  RECONCILED: "text-sky-800 font-medium",
  REVERSED: "text-gray-600",
};

const STATUS_TONE: Record<string, string> = {
  matched: "border-emerald-300 bg-emerald-50 text-emerald-800",
  needs_review: "border-amber-300 bg-amber-50 text-amber-800",
  unmatched: "border-gray-300 bg-gray-50 text-gray-700",
};

const fmt = (n: number) => (n ? n.toLocaleString("vi-VN") : "");
const fmtDate = (s: string) => {
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleDateString("vi-VN");
};

export function BankStatementPanel({
  voucherId,
  transactions,
  canReview,
  customers,
  suppliers,
  importNote,
  companyAccounts = [],
}: {
  voucherId: string;
  transactions: StatementTransactionView[];
  canReview: boolean;
  customers: PartyOption[];
  suppliers: PartyOption[];
  importNote: string | null;
  companyAccounts?: CompanyAccountOption[];
}) {
  const [settleOpen, setSettleOpen] = useState<string | null>(null);
  const [companyAccount, setCompanyAccount] = useState<string>("");
  const unlinked = transactions.some((t) => !t.companyBankAccountId && t.accountingStatus !== "POSTED");
  const router = useRouter();
  const [open, setOpen] = useState<string | null>(null);
  const [owner, setOwner] = useState<string>("");
  const [save, setSave] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const post = async (url: string, body: unknown, key: string) => {
    setBusy(key);
    setError(null);
    try {
      const res = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      const json = (await res.json().catch(() => ({}))) as { message?: string; error?: string };
      if (!res.ok) throw new Error(res.status === 403 ? "Chỉ ADMIN cùng phòng ban mới được xác nhận." : json.message ?? json.error ?? `HTTP ${res.status}`);
      setOpen(null);
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Lỗi mạng");
    } finally {
      setBusy(null);
    }
  };

  const openReview = (t: StatementTransactionView) => {
    const ev = parseMatchEvidence(t.matchEvidenceJson);
    setOwner(t.customerId ?? t.supplierId ?? ev.candidates.find((c) => c.owner_id)?.owner_id ?? "");
    setSave(false);
    setError(null);
    setOpen(open === t.id ? null : t.id);
  };

  const counts = transactions.reduce<Record<string, number>>((acc, t) => {
    const k = t.matchStatus ?? "unmatched";
    acc[k] = (acc[k] ?? 0) + 1;
    return acc;
  }, {});

  return (
    <section className="space-y-3 rounded-[var(--radius)] border p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-serif text-lg">Giao dịch sao kê ({transactions.length})</h2>
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className={`rounded-full border px-2 py-0.5 ${STATUS_TONE.matched}`}>MATCHED {counts.matched ?? 0}</span>
          <span className={`rounded-full border px-2 py-0.5 ${STATUS_TONE.needs_review}`}>NEEDS_REVIEW {counts.needs_review ?? 0}</span>
          <span className={`rounded-full border px-2 py-0.5 ${STATUS_TONE.unmatched}`}>UNMATCHED {counts.unmatched ?? 0}</span>
          {canReview && (
            <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => post(`/api/vouchers/${voucherId}/bank-transactions/rematch`, {}, "rematch")}>
              {busy === "rematch" ? <Loader2 className="size-3.5 animate-spin" /> : <RefreshCw className="size-3.5" />} Nhận diện lại
            </Button>
          )}
        </div>
      </div>
      <p className="text-xs text-[var(--color-muted-fg)]">
        Nhận diện khách hàng / nhà cung cấp cho từng giao dịch. Tiền vào chỉ được ghi nhận kế toán và đối trừ công nợ khi ADMIN bấm “Duyệt &amp; ghi nhận” trong “Ghi nhận công nợ”.
        {importNote ? ` ${importNote}` : ""}
      </p>

      {unlinked && (
        <div className="flex flex-wrap items-center gap-2 rounded-[var(--radius)] border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          <span>Chưa liên kết tài khoản ngân hàng của công ty cho sao kê này — chưa thể ghi nhận kế toán.</span>
          {canReview && (companyAccounts.length > 0 ? (
            <>
              <select aria-label="Tài khoản công ty" value={companyAccount} onChange={(e) => setCompanyAccount(e.target.value)} className="h-8 rounded border px-2 text-sm">
                <option value="">— Chọn tài khoản ngân hàng của công ty —</option>
                {companyAccounts.map((a) => <option key={a.id} value={a.id}>{a.bankName} {a.accountNumber} (TK {a.glAccountCode})</option>)}
              </select>
              <Button size="sm" disabled={!companyAccount || busy !== null}
                onClick={() => post(`/api/vouchers/${voucherId}/bank-transactions/company-account`, { bankAccountId: companyAccount }, "link")}>
                {busy === "link" ? <Loader2 className="size-3.5 animate-spin" /> : null} Liên kết
              </Button>
            </>
          ) : <span>Chưa có tài khoản ngân hàng công ty nào được đăng ký.</span>)}
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="w-full min-w-[1100px] text-sm">
          <thead className="text-left text-xs text-[var(--color-muted-fg)]">
            <tr>
              <th className="px-2 py-1">Ngày</th>
              <th className="px-2 py-1">Nội dung</th>
              <th className="px-2 py-1">TK đối ứng</th>
              <th className="px-2 py-1">Tên đối ứng</th>
              <th className="px-2 py-1 text-right">Tiền vào</th>
              <th className="px-2 py-1 text-right">Tiền ra</th>
              <th className="px-2 py-1">Khách hàng / NCC</th>
              <th className="px-2 py-1">Phương thức match</th>
              <th className="px-2 py-1 text-right">Độ tin cậy</th>
              <th className="px-2 py-1">Trạng thái</th>
              <th className="px-2 py-1">Kế toán</th>
              <th className="px-2 py-1" />
            </tr>
          </thead>
          <tbody>
            {transactions.map((t) => {
              const ev = parseMatchEvidence(t.matchEvidenceJson);
              const party = t.customerName ?? t.supplierName;
              const options = t.direction === "money_in" ? customers : suppliers;
              const chosen = options.find((o) => o.id === owner);
              const status = t.matchStatus ?? "unmatched";
              return (
                <Fragment key={t.id}>
                  <tr className="border-t align-top">
                    <td className="px-2 py-1 whitespace-nowrap">{fmtDate(t.txDate)}</td>
                    <td className="px-2 py-1">{t.description}</td>
                    <td className="px-2 py-1 font-mono">{t.counterpartyAccount ?? "—"}{t.counterpartyBankCode ? <span className="text-xs text-[var(--color-muted-fg)]"> {t.counterpartyBankCode}</span> : null}</td>
                    <td className="px-2 py-1">{t.counterpartyName ?? "—"}</td>
                    <td className="px-2 py-1 text-right font-mono text-emerald-800">{t.direction === "money_in" ? fmt(t.amount) : ""}</td>
                    <td className="px-2 py-1 text-right font-mono text-red-800">{t.direction === "money_out" ? fmt(t.amount) : ""}</td>
                    <td className="px-2 py-1">
                      {party ?? <span className="text-[var(--color-muted-fg)]">Chưa xác định</span>}
                      {t.supplierName && <span className="text-[10px] text-[var(--color-muted-fg)]"> (NCC)</span>}
                    </td>
                    <td className="px-2 py-1 text-xs">{t.matchMethod ? MATCH_METHOD_LABEL[t.matchMethod] ?? t.matchMethod : "—"}</td>
                    <td className="px-2 py-1 text-right font-mono text-xs">{t.matchConfidence != null && status === "matched" ? `${Math.round(t.matchConfidence * 100)}%` : "—"}</td>
                    <td className="px-2 py-1">
                      <span className={`rounded-full border px-2 py-0.5 text-[11px] ${STATUS_TONE[status] ?? ""}`}>{status.toUpperCase()}</span>
                      {t.confirmedBy && <div className="text-[10px] text-[var(--color-muted-fg)]">ADMIN xác nhận</div>}
                    </td>
                    <td className={`px-2 py-1 text-xs whitespace-nowrap ${ACCOUNTING_TONE[t.accountingStatus] ?? ""}`}>
                      {t.direction === "money_in" ? ACCOUNTING_LABEL[t.accountingStatus] ?? t.accountingStatus : "—"}
                    </td>
                    <td className="px-2 py-1 text-right whitespace-nowrap">
                      {t.direction === "money_in" && (t.matchStatus === "matched" || t.accountingStatus !== "NOT_POSTED") && (
                        <Button size="sm" variant="ghost" onClick={() => setSettleOpen(settleOpen === t.id ? null : t.id)}>Ghi nhận công nợ</Button>
                      )}
                      <Button size="sm" variant="ghost" onClick={() => openReview(t)}>
                        {canReview ? (t.direction === "money_in" ? "Chọn khách hàng" : "Chọn NCC") : "Chi tiết"}
                      </Button>
                    </td>
                  </tr>
                  {settleOpen === t.id && (
                    <tr>
                      <td colSpan={12} className="px-3 py-2">
                        <StatementSettlementPanel voucherId={voucherId} lineId={t.id} canApprove={canReview} />
                      </td>
                    </tr>
                  )}
                  {open === t.id && (
                    <tr className="bg-[var(--color-muted)]/30">
                      <td colSpan={12} className="space-y-2 px-3 py-2 text-xs">
                        {ev.signals.length > 0 && (
                          <div><b>Căn cứ:</b><ul className="list-disc pl-5">{ev.signals.map((s, i) => <li key={i}>{MATCH_METHOD_LABEL[s.method] ?? s.method}: {s.detail}</li>)}</ul></div>
                        )}
                        {ev.warnings.length > 0 && (
                          <div className="text-amber-800"><b>Cảnh báo:</b><ul className="list-disc pl-5">{ev.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul></div>
                        )}
                        {ev.decisions.length > 0 && (
                          <div className="text-[var(--color-muted-fg)]">Đã có {ev.decisions.length} quyết định ADMIN (lưu vết).</div>
                        )}
                        {canReview && (
                          <div className="flex flex-wrap items-center gap-2">
                            <select aria-label="Đối tác" value={owner} onChange={(e) => setOwner(e.target.value)} className="h-8 rounded border px-2 text-sm">
                              <option value="">— {t.direction === "money_in" ? "Chọn khách hàng" : "Chọn nhà cung cấp"} —</option>
                              {options.map((o) => <option key={o.id} value={o.id}>{o.name} ({o.code})</option>)}
                            </select>
                            {t.counterpartyAccount && chosen && !(t.counterpartyAccountKnownForOwner && (t.customerId ?? t.supplierId) === owner) && (
                              <label className="flex items-center gap-1">
                                <input type="checkbox" checked={save} onChange={(e) => setSave(e.target.checked)} />
                                Lưu tài khoản {t.counterpartyAccount} cho {chosen.name}
                              </label>
                            )}
                            <Button size="sm" disabled={!owner || busy !== null}
                              onClick={() => post(`/api/vouchers/${voucherId}/bank-transactions/${t.id}/match`, { ownerId: owner, saveBankAccount: save }, t.id)}>
                              {busy === t.id ? <Loader2 className="size-3.5 animate-spin" /> : null} Xác nhận
                            </Button>
                          </div>
                        )}
                        {error && <p className="text-red-700">Lỗi: {error}</p>}
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}
