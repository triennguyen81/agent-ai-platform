"use client";

import { useEffect, useState } from "react";

// "Chiết khấu thanh toán" — the Contract's STRUCTURED, deterministic
// early-payment term (X% if paid within N days of the invoice date). Only a
// department ADMIN edits it, only while the contract is still editable; it
// is used for settlement once the contract is approved, and every change is a
// new revision.

interface Term {
  id: string; revision: number; enabled: boolean; ratePercent: number; basis: string; daysCount: number;
  currency: string; note?: string | null; sourceReference?: string | null; createdAt: string; supersededAt?: string | null;
}
interface View {
  discount: { workflowState: string; editable: boolean; effective: boolean; current?: Term | null; revisions: Term[] };
  canEdit: boolean;
}

export function ContractPaymentDiscount({ contractId }: { contractId: string }) {
  const [view, setView] = useState<View | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState({ enabled: true, ratePercent: "", daysCount: "", note: "", sourceReference: "" });

  async function load() {
    const res = await fetch(`/api/contracts/${contractId}/payment-discount`, { cache: "no-store" });
    if (!res.ok) { setView(null); return; }
    const v = (await res.json()) as View;
    setView(v);
    const c = v.discount.current;
    if (c) setForm({ enabled: c.enabled, ratePercent: String(c.ratePercent), daysCount: String(c.daysCount), note: c.note ?? "", sourceReference: c.sourceReference ?? "" });
  }
  useEffect(() => { void load(); }, [contractId]); // eslint-disable-line react-hooks/exhaustive-deps

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const res = await fetch(`/api/contracts/${contractId}/payment-discount`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          enabled: form.enabled, ratePercent: Number(form.ratePercent), basis: "invoice_date",
          daysCount: Number(form.daysCount), note: form.note || null, sourceReference: form.sourceReference || null,
        }),
      });
      if (!res.ok) {
        const body = (await res.json().catch(() => ({}))) as { message?: string; errors?: Record<string, string[]> };
        setError(body.message ?? Object.values(body.errors ?? {}).flat().join(" ") ?? "Không lưu được.");
        return;
      }
      await load();
    } finally {
      setSaving(false);
    }
  }

  if (!view) return null;
  const { discount, canEdit } = view;
  const c = discount.current;
  return (
    <section className="rounded-lg border p-4" data-testid="contract-payment-discount">
      <h2 className="font-medium">Chiết khấu thanh toán</h2>
      <p className="text-xs text-[var(--color-muted-fg)]">
        Điều khoản có cấu trúc, tính tất định — chỉ dùng khi hợp đồng đã duyệt và hoá đơn được liên kết rõ với hợp đồng. ADMIN vẫn phải xác nhận khi ghi nhận khoản thu.
      </p>
      {c ? (
        <p className="mt-2 text-sm" data-testid="contract-payment-discount-current">
          {c.enabled ? "Bật" : "Tắt"} · {c.ratePercent}% nếu thanh toán trong {c.daysCount} ngày kể từ ngày hoá đơn · {c.currency} · phiên bản {c.revision}
          {" · "}
          <span className={discount.effective ? "text-emerald-700" : "text-amber-700"}>
            {discount.effective ? "Đang hiệu lực" : "Chưa hiệu lực (hợp đồng chưa duyệt hoặc đang tắt)"}
          </span>
        </p>
      ) : (
        <p className="mt-2 text-sm text-[var(--color-muted-fg)]">Hợp đồng không có điều khoản chiết khấu thanh toán.</p>
      )}
      {canEdit && (
        <div className="mt-3 grid gap-2 text-sm sm:grid-cols-2">
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={form.enabled} onChange={(e) => setForm({ ...form, enabled: e.target.checked })} />
            Bật chiết khấu thanh toán
          </label>
          <span className="text-[var(--color-muted-fg)]">Mốc tính: ngày hoá đơn</span>
          <label>Tỷ lệ (%)
            <input aria-label="Tỷ lệ chiết khấu" className="ml-2 w-24 rounded border px-1" inputMode="decimal" value={form.ratePercent}
              onChange={(e) => setForm({ ...form, ratePercent: e.target.value })} />
          </label>
          <label>Thanh toán trong (ngày)
            <input aria-label="Số ngày" className="ml-2 w-20 rounded border px-1" inputMode="numeric" value={form.daysCount}
              onChange={(e) => setForm({ ...form, daysCount: e.target.value })} />
          </label>
          <label className="sm:col-span-2">Dẫn chiếu điều khoản
            <input className="ml-2 w-64 rounded border px-1" value={form.sourceReference} onChange={(e) => setForm({ ...form, sourceReference: e.target.value })} />
          </label>
          <div className="sm:col-span-2">
            <button type="button" className="rounded border px-3 py-1 disabled:opacity-50" disabled={saving} onClick={save}>
              {saving ? "Đang lưu…" : "Lưu điều khoản"}
            </button>
          </div>
        </div>
      )}
      {!canEdit && discount.editable === false && c && (
        <p className="mt-2 text-xs text-[var(--color-muted-fg)]">Hợp đồng đã khoá — điều khoản không thể sửa.</p>
      )}
      {error && <p className="mt-2 text-sm text-red-700">{error}</p>}
      {discount.revisions.length > 1 && (
        <p className="mt-2 text-xs text-[var(--color-muted-fg)]">Lịch sử: {discount.revisions.length} phiên bản.</p>
      )}
    </section>
  );
}
