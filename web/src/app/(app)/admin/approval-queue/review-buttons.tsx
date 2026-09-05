"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Check, Loader2, X } from "lucide-react";

interface ReviewButtonsProps {
  id: string;
}

export function ReviewButtons({ id }: ReviewButtonsProps) {
  const router = useRouter();
  const [action, setAction] = useState<"approved" | "rejected" | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState<"approved" | "rejected" | null>(null);

  const submit = async (decision: "approved" | "rejected") => {
    if (busy) return;
    setBusy(true);
    setAction(decision);
    try {
      const res = await fetch(`/api/approval-queue/${id}/review`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ decision, note: note.trim() || null }),
      });
      if (res.ok) {
        router.refresh();
      } else {
        const body = (await res.json().catch(() => ({}))) as { error?: string };
        alert(`Lỗi: ${body.error ?? res.status}`);
        setAction(null);
      }
    } catch (err) {
      alert(err instanceof Error ? err.message : "Network error");
      setAction(null);
    } finally {
      setBusy(false);
    }
  };

  if (action) {
    return (
      <div className="shrink-0 rounded-[var(--radius)] bg-emerald-50 px-3 py-2 text-xs text-emerald-800">
        {action === "approved" ? "✓ Đã duyệt" : "✗ Đã từ chối"}
      </div>
    );
  }

  if (confirming) {
    return (
      <div className="shrink-0 w-64 space-y-2 rounded-[var(--radius)] border p-3">
        <p className="text-xs font-medium">
          Xác nhận {confirming === "approved" ? "duyệt" : "từ chối"} voucher này?
        </p>
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder="Ghi chú (tuỳ chọn)"
          className="w-full rounded border p-2 text-xs"
          rows={2}
        />
        <div className="flex gap-2">
          <button
            type="button"
            onClick={() => submit(confirming)}
            disabled={busy}
            className={`flex-1 rounded-[var(--radius)] px-3 py-1.5 text-xs font-medium text-white ${
              confirming === "approved" ? "bg-emerald-600 hover:bg-emerald-700" : "bg-red-600 hover:bg-red-700"
            }`}
          >
            {busy ? <Loader2 className="mx-auto size-3 animate-spin" /> : "Xác nhận"}
          </button>
          <button
            type="button"
            onClick={() => setConfirming(null)}
            disabled={busy}
            className="rounded-[var(--radius)] border px-3 py-1.5 text-xs hover:bg-[var(--color-muted)]"
          >
            Huỷ
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex shrink-0 flex-col gap-2">
      <button
        type="button"
        onClick={() => setConfirming("approved")}
        className="inline-flex items-center gap-1 rounded-[var(--radius)] bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-700"
      >
        <Check className="size-3.5" /> Duyệt
      </button>
      <button
        type="button"
        onClick={() => setConfirming("rejected")}
        className="inline-flex items-center gap-1 rounded-[var(--radius)] border border-red-300 px-3 py-1.5 text-xs font-medium text-red-700 hover:bg-red-50"
      >
        <X className="size-3.5" /> Từ chối
      </button>
    </div>
  );
}
