"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, ShieldAlert, ShieldCheck, ShieldQuestion } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  TAX_ACTOR_LABEL,
  TAX_AUTHORITY_LABEL,
  TAX_ERROR_LABEL,
  TAX_EVENT_LABEL,
  TAX_SOURCE_OPTIONS,
  TAX_RECOMMENDATION_LABEL,
  TAX_STATUS_LABEL,
  buildConfirmPayload,
  buildRejectPayload,
  rejectBlocked,
  confirmBlockers,
  describeProblems,
  initialRows,
  parseEvidence,
  taxStatusKey,
  type AttestationRow,
  type TaxVerificationResponse,
} from "@/lib/tax-verification";

// Xác thực hoá đơn với cơ quan thuế. Only a department ADMIN (never the
// claim's submitter) can verify, with evidence the Gateway matches to the
// voucher's CURRENT invoice data. The Agent can only propose.

const fmt = (n: number | null | undefined) => (n == null ? "—" : Number(n).toLocaleString("vi-VN"));
const fmtDate = (s: string | null | undefined) => (s ? new Date(s).toLocaleString("vi-VN") : "—");

const STATUS_CLASS: Record<string, string> = {
  none: "border-slate-300 bg-slate-50 text-slate-700",
  pending_review: "border-amber-300 bg-amber-50 text-amber-800",
  verified: "border-emerald-300 bg-emerald-50 text-emerald-800",
  invalid: "border-red-300 bg-red-50 text-red-800",
  unavailable: "border-orange-300 bg-orange-50 text-orange-800",
};

export function TaxVerificationPanel({ voucherId }: { voucherId: string }) {
  const router = useRouter();
  const [data, setData] = useState<TaxVerificationResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [rows, setRows] = useState<AttestationRow[]>([]);
  const [source, setSource] = useState(TAX_SOURCE_OPTIONS[0].value);
  const [note, setNote] = useState("");
  const [portalChecked, setPortalChecked] = useState(false);
  const [rejectReason, setRejectReason] = useState("");
  const [rejectSource, setRejectSource] = useState(TAX_SOURCE_OPTIONS[0].value);
  const [rejectLookup, setRejectLookup] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<{ message: string; problems: string[] } | null>(null);
  const [showForm, setShowForm] = useState(false);

  const load = useCallback(async () => {
    const res = await fetch(`/api/vouchers/${voucherId}/tax-verification`, { cache: "no-store" });
    if (!res.ok) {
      setLoadError(`HTTP ${res.status}`);
      return;
    }
    const json = (await res.json()) as TaxVerificationResponse;
    setData(json);
    setRows(initialRows(json.verification.invoices));
  }, [voucherId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function act(action: "confirm" | "reject", body: unknown) {
    setBusy(action);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/tax-verification/${action}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      const json = (await res.json().catch(() => ({}))) as { error?: string; message?: string; details?: unknown };
      if (!res.ok) {
        setError({
          message: (json.error && TAX_ERROR_LABEL[json.error]) ?? json.message ?? json.error ?? `HTTP ${res.status}`,
          problems: describeProblems(json.details),
        });
        return;
      }
      setShowForm(false);
      setNote("");
      setPortalChecked(false);
      setRejectReason("");
      await load();
      router.refresh();
    } finally {
      setBusy(null);
    }
  }

  if (loadError) return null;
  if (!data) {
    return (
      <section className="rounded-[var(--radius)] border p-4 text-sm text-[var(--color-muted-fg)]">
        <Loader2 className="mr-1 inline size-4 animate-spin" /> Đang tải trạng thái xác thực hoá đơn…
      </section>
    );
  }
  const v = data.verification;
  if (!v.verifiable) return null;
  const key = taxStatusKey(v);
  const evidence = parseEvidence(v.evidenceJson);
  const hints = confirmBlockers(note, portalChecked, rows);
  const Icon = key === "verified" ? ShieldCheck : key === "invalid" || key === "unavailable" ? ShieldAlert : ShieldQuestion;

  return (
    <section className="space-y-3 rounded-[var(--radius)] border p-4 text-sm" data-testid="tax-verification-panel" data-status={key}>
      <div className="flex flex-wrap items-center gap-2">
        <Icon className="size-4" />
        <span className="font-medium">XÁC MINH THUẾ</span>
        <span className={`rounded border px-1.5 py-0.5 text-xs font-medium ${STATUS_CLASS[key] ?? ""}`} data-testid="tax-status">
          {TAX_STATUS_LABEL[key] ?? key}
        </span>
        {v.requiredForPayment && (
          <span className="text-xs text-[var(--color-muted-fg)]">· Bắt buộc trước khi duyệt thanh toán</span>
        )}
      </div>

      <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5 text-xs" data-testid="tax-facts">
        <dt className="text-[var(--color-muted-fg)]">MST</dt>
        <dd className="font-mono">{[...new Set(v.invoices.map((i) => i.sellerMst).filter(Boolean))].join(", ") || "—"}</dd>
        <dt className="text-[var(--color-muted-fg)]">Trạng thái</dt>
        <dd>{TAX_STATUS_LABEL[key] ?? key}</dd>
        {key === "verified" && (
          <>
            <dt className="text-[var(--color-muted-fg)]">Nguồn</dt>
            <dd data-testid="tax-source">
              {TAX_AUTHORITY_LABEL[v.verificationSource ?? ""] ?? v.verificationSource ?? "—"}
              {evidence?.evidence_source && <> · đối chiếu trên: {TAX_SOURCE_OPTIONS.find((o) => o.value === evidence.evidence_source)?.label ?? evidence.evidence_source}</>}
            </dd>
            <dt className="text-[var(--color-muted-fg)]">Thời gian</dt>
            <dd>{fmtDate(v.checkedAt)}</dd>
            <dt className="text-[var(--color-muted-fg)]">Người xác nhận</dt>
            <dd data-testid="tax-verified-by">{v.verifiedByName ?? v.verifiedBy}</dd>
            <dt className="text-[var(--color-muted-fg)]">Bằng chứng</dt>
            <dd data-testid="tax-evidence">
              {evidence?.comment ?? "—"}
              {(evidence?.invoices ?? []).filter((i) => i.lookup_reference).map((i) => (
                <span key={i.invoice_number} className="ml-1 font-mono">[{i.invoice_number}: {i.lookup_reference}]</span>
              ))}
            </dd>
          </>
        )}
      </dl>
      {key !== "verified" && v.reason && <div className="text-xs text-amber-800">{v.reason}</div>}
      {v.proposedRecommendation && (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-1.5 text-xs text-amber-800" data-testid="tax-proposal">
          Có đề xuất: <b>{TAX_RECOMMENDATION_LABEL[v.proposedRecommendation] ?? v.proposedRecommendation}</b> — chưa có giá trị
          chính thức, chờ ADMIN quyết định kèm bằng chứng.
        </div>
      )}

      <table className="w-full text-xs">
        <thead className="text-left text-[var(--color-muted-fg)]">
          <tr><th className="py-0.5">MST bên bán</th><th>Ký hiệu</th><th>Số HĐ</th><th>Ngày</th><th className="text-right">Tổng tiền</th></tr>
        </thead>
        <tbody>
          {v.invoices.map((i, idx) => (
            <tr key={idx} className="border-t font-mono">
              <td className="py-0.5">{i.sellerMst || "—"}</td><td>{i.invoiceSeries ?? "—"}</td><td>{i.invoiceNumber}</td>
              <td>{i.invoiceDate ?? "—"}</td><td className="text-right">{fmt(i.totalAmount)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {!data.canConfirm && key !== "verified" && (
        <p className="text-xs text-[var(--color-muted-fg)]">Chỉ ADMIN phòng ban xác thực hoá đơn, kèm bằng chứng đối chiếu.</p>
      )}

      {data.canConfirm && key !== "verified" && !showForm && (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" onClick={() => { setRows(initialRows(v.invoices)); setShowForm(true); setError(null); }}>
            Xác nhận hợp lệ…
          </Button>
        </div>
      )}

      {showForm && (
        <div className="space-y-2 border-t pt-3" data-testid="tax-confirm-form">
          <label className="block text-xs">
            Nguồn đối chiếu
            <select className="mt-1 block w-full rounded border px-2 py-1" value={source} onChange={(e) => setSource(e.target.value)}>
              {TAX_SOURCE_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Nhập đúng thông tin hiển thị trên nguồn tra cứu. Gateway so khớp với dữ liệu chứng từ hiện tại; nếu khác, sửa chứng từ trước.
          </p>
          <div className="overflow-x-auto">
            <table className="w-full text-xs">
              <thead className="text-left text-[var(--color-muted-fg)]">
                <tr><th>MST bên bán</th><th>Ký hiệu</th><th>Số HĐ</th><th>Ngày</th><th>Tổng tiền</th><th>Mã tra cứu</th></tr>
              </thead>
              <tbody>
                {rows.map((r, idx) => (
                  <tr key={idx}>
                    {(["sellerMst", "invoiceSeries", "invoiceNumber", "invoiceDate", "totalAmount", "lookupCode"] as const).map((f) => (
                      <td key={f} className="pr-1">
                        <input
                          aria-label={`${f}-${idx}`}
                          type={f === "invoiceDate" ? "date" : f === "totalAmount" ? "number" : "text"}
                          className="w-full rounded border px-1 py-0.5 font-mono"
                          value={r[f]}
                          onChange={(e) => setRows(rows.map((x, i) => (i === idx ? { ...x, [f]: e.target.value } : x)))}
                        />
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <label className="block text-xs">
            Ghi chú bằng chứng (bắt buộc)
            <textarea aria-label="tax-note" className="mt-1 block w-full rounded border px-2 py-1" rows={2} value={note}
              placeholder="VD: Tra cứu ngày …, kết quả: Hoá đơn đã được cấp mã, trạng thái hợp lệ"
              onChange={(e) => setNote(e.target.value)} />
          </label>
          <label className="flex items-start gap-2 text-xs">
            <input aria-label="tax-attest" type="checkbox" checked={portalChecked} onChange={(e) => setPortalChecked(e.target.checked)} />
            Tôi đã tự đối chiếu từng hoá đơn trên nguồn đã chọn và thông tin trên khớp với kết quả tra cứu.
          </label>
          <div className="flex flex-wrap gap-2">
            <Button size="sm" disabled={busy !== null || hints.length > 0}
              onClick={() => act("confirm", buildConfirmPayload(source, note, portalChecked, rows))}>
              {busy === "confirm" ? <Loader2 className="size-3.5 animate-spin" /> : <ShieldCheck className="size-3.5" />} Xác nhận hợp lệ
            </Button>
            <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => setShowForm(false)}>Huỷ</Button>
          </div>
        </div>
      )}

      {data.canConfirm && (
        <details className="text-xs" data-testid="tax-reject">
          <summary className="cursor-pointer text-[var(--color-muted-fg)]">Đánh dấu không hợp lệ / không tra cứu được (ADMIN, cần bằng chứng)</summary>
          <div className="mt-2 space-y-2">
            <select aria-label="tax-reject-source" className="block w-full rounded border px-2 py-1" value={rejectSource} onChange={(e) => setRejectSource(e.target.value)}>
              {TAX_SOURCE_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
            <input aria-label="tax-reject-lookup" className="block w-full rounded border px-2 py-1 font-mono" placeholder="Mã tra cứu / tham chiếu (nếu có)"
              value={rejectLookup} onChange={(e) => setRejectLookup(e.target.value)} />
            <input aria-label="tax-reject-reason" className="block w-full rounded border px-2 py-1" placeholder="Lý do / kết quả tra cứu (tối thiểu 10 ký tự)"
              value={rejectReason} onChange={(e) => setRejectReason(e.target.value)} />
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" disabled={busy !== null || rejectBlocked(rejectReason, rejectSource)}
                onClick={() => act("reject", buildRejectPayload(rejectReason, rejectSource, false, rejectLookup))}>Đánh dấu không hợp lệ</Button>
              <Button size="sm" variant="outline" disabled={busy !== null || rejectBlocked(rejectReason, rejectSource)}
                onClick={() => act("reject", buildRejectPayload(rejectReason, rejectSource, true, rejectLookup))}>Không tra cứu được</Button>
            </div>
          </div>
        </details>
      )}

      {error && (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-800" data-testid="tax-error">
          {error.message}
          {error.problems.length > 0 && <ul className="mt-1 list-disc pl-4">{error.problems.map((p) => <li key={p}>{p}</li>)}</ul>}
        </div>
      )}

      {v.events.length > 0 && (
        <details className="text-xs" data-testid="tax-history">
          <summary className="cursor-pointer text-[var(--color-muted-fg)]">Lịch sử xác thực ({v.events.length})</summary>
          <ul className="mt-1 space-y-0.5">
            {v.events.map((e) => (
              <li key={e.id}>
                {fmtDate(e.createdAt)} · {TAX_EVENT_LABEL[e.event] ?? e.event} · {TAX_ACTOR_LABEL[e.actorType] ?? e.actorType}{e.actorRole ? `/${e.actorRole}` : ""}
                {e.source ? ` · ${TAX_AUTHORITY_LABEL[e.source] ?? e.source}` : ""}
                {e.actorName ? ` (${e.actorName})` : ""}{e.reason ? ` — ${e.reason}` : ""}
              </li>
            ))}
          </ul>
        </details>
      )}
    </section>
  );
}
