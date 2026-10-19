"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";

// Bill items of a multi-file Expense Claim (one file = one item). Shown on the
// claim's voucher page; renders nothing for a legacy single-document claim.
// Numbers here are the item-derived PREVIEW; the authoritative claim totals
// are the existing expense-claim result/recognition panels below.

interface Values { category?: string | null; grossAmount?: number | null }
interface Item {
  id: string; sequenceNo: number; originalFilename: string; status: string; reviewReasons: string[]; attempt: number;
  ocrConfidence?: number | null; extractionConfidence?: number | null;
  extractedCategory?: string | null; extractedMerchantName?: string | null; extractedMerchantTaxCode?: string | null;
  extractedDocumentNumber?: string | null; extractedDocumentDate?: string | null; extractedGrossAmount?: number | null;
  extractedVatAmount?: number | null; extractedCurrency?: string | null; extractedDescription?: string | null;
  confirmedCategory?: string | null; confirmedMerchantName?: string | null; confirmedMerchantTaxCode?: string | null;
  confirmedDocumentNumber?: string | null; confirmedDocumentDate?: string | null; confirmedGrossAmount?: number | null;
  confirmedVatAmount?: number | null; confirmedDescription?: string | null; confirmedBy?: string | null;
  excludedReason?: string | null; errorCode?: string | null; configSource?: string | null; agentVersion?: number | null;
  values: Values;
}
interface ClaimView {
  claim: {
    intake: { employeeCode: string; employeeName: string; purpose?: string | null; tripStart?: string | null; tripEnd?: string | null; outstandingAdvance: number };
    items: Item[];
    totals: { itemCount: number; readyCount: number; unresolvedCount: number; excludedCount: number; readyTotal: number; unresolvedKnownTotal: number; outstandingAdvance: number; expectedPayable?: number | null };
    result?: { approvedAmount: number; advanceAmount: number; payableAmount: number; outcome: string; reviewStatus: string; recognitionStatus: string } | null;
    locked: boolean;
    configuredCategories: string[];
  };
  canManageItems: boolean;
  canAddFiles: boolean;
}

const STATUS: Record<string, { label: string; tone: string }> = {
  processing: { label: "Đang xử lý", tone: "text-amber-700" },
  ready: { label: "Sẵn sàng", tone: "text-emerald-700" },
  needs_review: { label: "Cần kiểm tra", tone: "text-amber-800" },
  extraction_failed: { label: "Lỗi trích xuất", tone: "text-red-700" },
  excluded: { label: "Đã loại", tone: "text-[var(--color-muted-fg)] line-through" },
};
const REASONS: Record<string, string> = {
  amount_missing: "thiếu số tiền", date_missing: "thiếu ngày", category_unknown: "khoản mục chưa rõ",
  currency_unsupported: "ngoại tệ", low_confidence: "độ tin cậy thấp", low_ocr_confidence: "ảnh/OCR mờ",
  vat_unclear: "VAT không rõ", outside_trip_dates: "ngoài thời gian công tác", duplicate_file: "trùng file",
  duplicate_invoice: "trùng hoá đơn", duplicate_amount_date: "trùng số tiền + ngày", workflow_require_review: "quy trình yêu cầu duyệt",
};
const vnd = (n?: number | null) => (n == null ? "—" : new Intl.NumberFormat("vi-VN").format(n));

export function ExpenseClaimItemsPanel({ claimId }: { claimId: string }) {
  const router = useRouter();
  const [view, setView] = useState<ClaimView | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(async () => {
    const res = await fetch(`/api/expense-claims/${claimId}`, { cache: "no-store" });
    setView(res.ok ? ((await res.json()) as ClaimView) : null);
  }, [claimId]);
  useEffect(() => { void load(); }, [load]);
  // Poll while any bill is still being read.
  useEffect(() => {
    if (!view || !view.claim.items.some((i) => i.status === "processing")) return;
    const t = setTimeout(() => { void load(); router.refresh(); }, 5000);
    return () => clearTimeout(t);
  }, [view, load, router]);

  if (!view) return null;
  const { claim, canManageItems, canAddFiles } = view;
  const t = claim.totals;

  async function act(url: string, init: RequestInit) {
    setBusy(true);
    setMessage(null);
    try {
      const res = await fetch(url, { ...init, headers: { "Content-Type": "application/json", ...(init.headers ?? {}) } });
      if (!res.ok) {
        const b = (await res.json().catch(() => ({}))) as { message?: string; error?: string };
        setMessage(b.message ?? b.error ?? `Lỗi (HTTP ${res.status}).`);
        return false;
      }
      await load();
      router.refresh();
      return true;
    } finally {
      setBusy(false);
    }
  }

  async function addFiles(list: FileList | null) {
    if (!list || list.length === 0) return;
    const form = new FormData();
    form.append("requestId", crypto.randomUUID());
    for (const f of Array.from(list)) form.append("files", f, f.name);
    setBusy(true);
    setMessage(null);
    try {
      const res = await fetch(`/api/expense-claims/${claimId}/items`, { method: "POST", body: form });
      const b = (await res.json().catch(() => ({}))) as { error?: string; message?: string; files?: { fileName: string; outcome: string; error?: string }[] };
      if (!res.ok) setMessage(b.message ?? b.error ?? "Không thêm được file.");
      const bad = (b.files ?? []).filter((f) => f.outcome === "rejected");
      if (bad.length > 0) setMessage(bad.map((f) => `${f.fileName}: ${f.error}`).join("; "));
      await load();
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="space-y-3 rounded-[var(--radius)] border p-4" data-testid="expense-claim-items">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="font-serif text-xl">Các khoản chi ({t.itemCount} chứng từ)</h2>
        <span className="text-sm text-[var(--color-muted-fg)]">
          {claim.intake.employeeCode} · {claim.intake.employeeName}{claim.intake.purpose ? ` · ${claim.intake.purpose}` : ""}
          {claim.intake.tripStart || claim.intake.tripEnd ? ` · ${claim.intake.tripStart ?? "…"} → ${claim.intake.tripEnd ?? "…"}` : ""}
        </span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-[var(--color-muted-fg)]">
            <tr><th>#</th><th>File</th><th>Khoản mục</th><th>Người bán / Số CT / Ngày</th><th className="text-right">Số tiền</th><th className="text-right">VAT</th><th>Trạng thái</th><th /></tr>
          </thead>
          <tbody>
            {claim.items.map((i) => {
              const s = STATUS[i.status] ?? { label: i.status, tone: "" };
              const merchant = i.confirmedMerchantName ?? i.extractedMerchantName;
              const number = i.confirmedDocumentNumber ?? i.extractedDocumentNumber;
              const date = i.confirmedDocumentDate ?? i.extractedDocumentDate;
              const vat = i.confirmedBy ? i.confirmedVatAmount : i.extractedVatAmount;
              return (
                <tr key={i.id} className="border-t align-top" data-testid="expense-claim-item" data-status={i.status}>
                  <td className="py-1 pr-2">{i.sequenceNo}</td>
                  <td className="pr-2"><a className="underline" href={`/api/expense-claims/${claimId}/items/${i.id}/file`} target="_blank" rel="noreferrer">{i.originalFilename}</a></td>
                  <td className="pr-2">{i.values.category ?? "—"}</td>
                  <td className="pr-2">{merchant ?? "—"}<div className="text-xs text-[var(--color-muted-fg)]">{number ?? "—"} · {date ?? "—"}</div></td>
                  <td className="pr-2 text-right font-mono">{vnd(i.values.grossAmount)}</td>
                  <td className="pr-2 text-right font-mono">{vnd(vat)}</td>
                  <td className={`pr-2 ${s.tone}`}>
                    {s.label}{i.confirmedBy ? " · đã xác nhận" : ""}
                    {i.reviewReasons.length > 0 && <div className="text-xs">{i.reviewReasons.map((r) => REASONS[r] ?? r).join(", ")}</div>}
                    {i.errorCode && <div className="text-xs">{i.errorCode}</div>}
                    {i.excludedReason && <div className="text-xs">Lý do: {i.excludedReason}</div>}
                    {i.extractionConfidence != null && <div className="text-xs text-[var(--color-muted-fg)]">tin cậy {Math.round(i.extractionConfidence * 100)}%{i.configSource ? ` · ${i.configSource}${i.agentVersion ? ` v${i.agentVersion}` : ""}` : ""}</div>}
                  </td>
                  <td className="whitespace-nowrap text-xs">
                    {canManageItems && i.status !== "processing" && i.status !== "excluded" && (
                      <button type="button" className="mr-2 underline" disabled={busy} onClick={() => setEditing(editing === i.id ? null : i.id)}>Kiểm tra</button>
                    )}
                    {canManageItems && (i.status === "extraction_failed" || i.status === "needs_review") && (
                      <button type="button" className="mr-2 underline" disabled={busy}
                        onClick={() => act(`/api/expense-claims/${claimId}/items/${i.id}/retry`, { method: "POST", body: "{}" })}>Đọc lại</button>
                    )}
                    {canManageItems && i.status !== "excluded" && i.status !== "processing" && (
                      <button type="button" className="text-red-700 underline" disabled={busy}
                        onClick={() => { const reason = window.prompt("Lý do loại khoản chi này?"); if (reason) void act(`/api/expense-claims/${claimId}/items/${i.id}/exclude`, { method: "POST", body: JSON.stringify({ reason }) }); }}>Loại</button>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {editing && (
        <ConfirmItemForm key={editing} item={claim.items.find((i) => i.id === editing)!} categories={claim.configuredCategories} busy={busy}
          onCancel={() => setEditing(null)}
          onSubmit={async (body) => { if (await act(`/api/expense-claims/${claimId}/items/${editing}`, { method: "PATCH", body: JSON.stringify(body) })) setEditing(null); }} />
      )}
      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 rounded bg-[var(--color-muted)] p-3 text-sm sm:grid-cols-4" data-testid="expense-claim-totals">
        <dt>Tổng đã xác nhận</dt><dd className="text-right font-mono" data-testid="claim-ready-total">{vnd(t.readyTotal)}</dd>
        <dt>Đang xử lý / cần kiểm tra</dt><dd className="text-right font-mono">{t.unresolvedCount > 0 ? `${vnd(t.unresolvedKnownTotal)} (${t.unresolvedCount})` : "0"}</dd>
        <dt>Tạm ứng</dt><dd className="text-right font-mono">{vnd(t.outstandingAdvance)}</dd>
        <dt>Dự kiến công ty trả</dt>
        <dd className="text-right font-mono" data-testid="claim-expected-payable">
          {t.expectedPayable != null ? vnd(t.expectedPayable) : t.unresolvedCount > 0 ? "chờ xử lý xong" : "—"}
        </dd>
      </dl>
      {t.unresolvedCount > 0 && (
        <p className="text-sm text-amber-800">Còn {t.unresolvedCount} khoản chi cần xử lý — chưa thể duyệt đề nghị cho đến khi xác nhận hoặc loại.</p>
      )}
      {claim.locked && <p className="text-sm text-[var(--color-muted-fg)]">Đề nghị đã ghi nhận kế toán — các khoản chi đã khoá.</p>}
      {canAddFiles && (
        <label className="inline-block cursor-pointer rounded border px-3 py-1 text-sm">
          Thêm chứng từ
          <input type="file" multiple accept="application/pdf,image/jpeg,image/png,image/webp" className="hidden" disabled={busy}
            onChange={(e) => void addFiles(e.target.files)} />
        </label>
      )}
      {message && <p className="text-sm text-red-700" data-testid="expense-claim-items-error">{message}</p>}
    </section>
  );
}

function ConfirmItemForm({ item, categories, busy, onCancel, onSubmit }: {
  item: Item; categories: string[]; busy: boolean; onCancel: () => void; onSubmit: (body: Record<string, unknown>) => Promise<void>;
}) {
  const [f, setF] = useState({
    category: item.confirmedCategory ?? (categories.includes(item.extractedCategory ?? "") ? item.extractedCategory! : ""),
    merchantName: item.confirmedMerchantName ?? item.extractedMerchantName ?? "",
    merchantTaxCode: item.confirmedMerchantTaxCode ?? item.extractedMerchantTaxCode ?? "",
    documentNumber: item.confirmedDocumentNumber ?? item.extractedDocumentNumber ?? "",
    documentDate: item.confirmedDocumentDate ?? item.extractedDocumentDate ?? "",
    grossAmount: String(item.confirmedGrossAmount ?? item.extractedGrossAmount ?? ""),
    vatAmount: String(item.confirmedVatAmount ?? item.extractedVatAmount ?? ""),
    description: item.confirmedDescription ?? item.extractedDescription ?? "",
    note: "",
  });
  const input = (k: keyof typeof f, label: string, type = "text") => (
    <label className="text-xs">{label}
      <input aria-label={label} type={type} className="mt-0.5 block w-full rounded border px-1 py-0.5 text-sm" value={f[k]} onChange={(e) => setF({ ...f, [k]: e.target.value })} />
    </label>
  );
  return (
    <div className="grid gap-2 rounded border border-amber-300 bg-amber-50 p-3 sm:grid-cols-4" data-testid="expense-claim-item-form">
      <div className="sm:col-span-4 text-sm font-medium">Xác nhận khoản chi #{item.sequenceNo} — {item.originalFilename}</div>
      <label className="text-xs">Khoản mục
        <select aria-label="Khoản mục" className="mt-0.5 block w-full rounded border px-1 py-0.5 text-sm" value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })}>
          <option value="">— chọn —</option>
          {categories.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
      </label>
      {input("merchantName", "Người bán")}
      {input("merchantTaxCode", "MST người bán")}
      {input("documentNumber", "Số chứng từ")}
      {input("documentDate", "Ngày chứng từ", "date")}
      {input("grossAmount", "Số tiền")}
      {input("vatAmount", "Tiền VAT (nếu có)")}
      {input("description", "Mô tả")}
      <div className="sm:col-span-3">{input("note", "Ghi chú xác nhận")}</div>
      <div className="flex items-end gap-2">
        <button type="button" className="rounded bg-[var(--color-primary)] px-3 py-1 text-sm text-white disabled:opacity-50" disabled={busy || !f.category}
          onClick={() => void onSubmit({
            category: f.category, merchantName: f.merchantName || null, merchantTaxCode: f.merchantTaxCode || null,
            documentNumber: f.documentNumber || null, documentDate: f.documentDate || null,
            grossAmount: f.grossAmount ? Number(f.grossAmount) : null, vatAmount: f.vatAmount ? Number(f.vatAmount) : null,
            currency: "VND", description: f.description || null, note: f.note || null,
          })}>Xác nhận</button>
        <button type="button" className="text-sm underline" onClick={onCancel}>Huỷ</button>
      </div>
    </div>
  );
}
