"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, ScanSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  SELECTABLE_TYPES,
  SOURCE_LABEL,
  confidenceBand,
  mismatchChoices,
  voucherTypeLabel,
  type VoucherTypeClassificationView,
} from "@/lib/voucher-type";

// AIQ voucher-type detection result + ADMIN confirm/correct. The gateway
// re-validates everything (department_admin, not posted, same department).

const BAND_TONE = {
  high: "border-emerald-300 bg-emerald-50 text-emerald-800",
  medium: "border-amber-300 bg-amber-50 text-amber-800",
  low: "border-red-300 bg-red-50 text-red-700",
} as const;

export function VoucherTypeReview({
  voucherId,
  currentType,
  classification,
  canDecide,
  posted,
  compact = false,
  onChanged,
}: {
  voucherId: string;
  currentType: string;
  classification: VoucherTypeClassificationView | null;
  canDecide: boolean;
  posted: boolean;
  compact?: boolean;
  onChanged?: () => void;
}) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [picked, setPicked] = useState<string>(currentType === "unclassified" ? "" : currentType);

  const decide = async (voucherType: string) => {
    setBusy(voucherType);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/voucher-type`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ voucherType }),
      });
      const body = (await res.json().catch(() => ({}))) as { message?: string; error?: string };
      if (!res.ok) {
        throw new Error(res.status === 403 ? "Chỉ ADMIN cùng phòng ban mới được xác nhận/đổi loại chứng từ." : body.message ?? body.error ?? `HTTP ${res.status}`);
      }
      setEditing(false);
      onChanged?.();
      router.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Lỗi mạng");
    } finally {
      setBusy(null);
    }
  };

  const c = classification;
  const detected = c?.detected_voucher_type ?? (c ? c.voucher_type : null);
  const pct = c ? Math.round(c.confidence * 100) : null;
  const band = c ? confidenceBand(c.confidence) : "low";
  const mismatch = mismatchChoices(c);
  const confirmed = !!c?.confirmed_by;
  const needsDecision = !!c && c.requires_confirmation && !confirmed;
  const allowControls = canDecide && !posted;

  const picker = (
    <div className="flex flex-wrap items-center gap-2">
      <select
        aria-label="Loại chứng từ"
        value={picked}
        onChange={(e) => setPicked(e.target.value)}
        className="h-8 rounded border px-2 text-sm"
        disabled={busy !== null}
      >
        <option value="" disabled>— Chọn loại đúng —</option>
        {SELECTABLE_TYPES.map((t) => (
          <option key={t.value} value={t.value}>{t.label}</option>
        ))}
      </select>
      <Button size="sm" disabled={!picked || busy !== null} onClick={() => decide(picked)}>
        {busy === picked ? <Loader2 className="size-3.5 animate-spin" /> : null} Xác nhận loại
      </Button>
    </div>
  );

  return (
    <section className={`space-y-2 rounded-[var(--radius)] border ${compact ? "p-2 text-xs" : "p-4 text-sm"} ${needsDecision ? "border-amber-300 bg-amber-50/40" : ""}`}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <ScanSearch className="size-4 text-[var(--color-muted-fg)]" />
        <span>AIQ nhận diện: <b>{c ? voucherTypeLabel(detected) : "—"}</b></span>
        {pct !== null && (
          <span className={`rounded-full border px-2 py-0.5 text-[11px] font-medium ${BAND_TONE[band]}`}>
            Độ tin cậy {pct}%
          </span>
        )}
        <span className="text-[var(--color-muted-fg)]">
          Loại đang dùng: <b className="text-[var(--color-fg)]">{voucherTypeLabel(currentType)}</b>
          {c && <> · {confirmed ? "ADMIN đã xác nhận" : SOURCE_LABEL[c.source] ?? c.source}</>}
        </span>
      </div>

      {c && c.evidence.length > 0 && !compact && (
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Căn cứ:</div>
          <ul className="list-disc pl-5 text-xs">
            {c.evidence.map((e, i) => <li key={i}>{e}</li>)}
          </ul>
        </div>
      )}

      {!c && currentType === "unclassified" && (
        <p className="text-xs text-amber-800">Chưa nhận diện được loại chứng từ.</p>
      )}

      {mismatch && !confirmed && (
        <div className="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          <div>Loại chứng từ đã chọn: <b>{voucherTypeLabel(mismatch.keep)}</b></div>
          <div>AIQ nhận diện: <b>{voucherTypeLabel(mismatch.detected)}</b>{pct !== null ? ` (${pct}%)` : ""}</div>
          <div className="mt-1 font-medium">⚠ Loại đã chọn khác kết quả nhận diện. Vui lòng xác nhận loại đúng.</div>
          {allowControls && (
            <div className="mt-2 flex flex-wrap gap-2">
              <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => decide(mismatch.keep)}>
                {busy === mismatch.keep ? <Loader2 className="size-3.5 animate-spin" /> : null} Giữ loại đã chọn
              </Button>
              <Button size="sm" disabled={busy !== null} onClick={() => decide(mismatch.detected)}>
                {busy === mismatch.detected ? <Loader2 className="size-3.5 animate-spin" /> : null} Dùng loại AIQ nhận diện
              </Button>
            </div>
          )}
        </div>
      )}

      {c?.status === "conflict" && !confirmed && (
        <div className="rounded border border-red-300 bg-red-50 px-3 py-2 text-xs text-red-800">
          ⚠ Tiêu đề chứng từ và AI trích xuất không thống nhất
          {c.llm_voucher_type ? ` (AI: ${voucherTypeLabel(c.llm_voucher_type)})` : ""} — cần ADMIN xác nhận loại đúng trước khi ghi nhận.
          {allowControls && <div className="mt-2">{picker}</div>}
        </div>
      )}

      {c && !mismatch && c.status === "needs_confirmation" && !confirmed && (
        <div className="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          Độ tin cậy thấp — cần xác nhận loại chứng từ trước khi ghi nhận.
          {allowControls && <div className="mt-2">{picker}</div>}
        </div>
      )}

      {c?.status === "confirm_recommended" && !confirmed && (
        <div className="flex flex-wrap items-center gap-2 text-xs text-amber-800">
          <span>Độ tin cậy trung bình — nên kiểm tra lại loại chứng từ.</span>
          {allowControls && (
            <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => decide(currentType)}>
              Xác nhận {voucherTypeLabel(currentType)}
            </Button>
          )}
        </div>
      )}

      {!c && currentType === "unclassified" && allowControls && picker}

      {allowControls && !needsDecision && !compact && (
        editing ? picker : (
          <button type="button" className="text-xs text-[var(--color-primary)] hover:underline" onClick={() => setEditing(true)}>
            Đổi loại chứng từ
          </button>
        )
      )}

      {needsDecision && posted === false && !canDecide && (
        <p className="text-xs text-amber-800">Chờ ADMIN cùng phòng ban xác nhận loại chứng từ.</p>
      )}

      {error && <p className="text-xs text-red-700">Lỗi: {error}</p>}
    </section>
  );
}
