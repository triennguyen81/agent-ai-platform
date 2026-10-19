"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { CheckCircle2, Loader2, ShieldAlert, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";

export interface DocumentReviewTask {
  id: string;
  status: "open" | "approved" | "rejected";
  documentType: string;
  reasons: string[];
  createdAt: string;
  decidedAt: string | null;
  decisionNote: string | null;
}

const STATUS_LABEL: Record<string, string> = {
  awaiting_review: "Đang chờ duyệt theo workflow — chưa thể ghi nhận",
  approved: "Đã duyệt theo workflow",
  rejected: "Bị từ chối theo workflow — không được ghi nhận",
  review_task_creation_failed: "Không tạo được bước duyệt — chứng từ bị chặn (cần trích xuất lại)",
};

// Workflow require_review gate. Approve/Reject is shown to ADMIN only; the
// Gateway is the authority (department_admin + same department) — USER,
// TENANT_OWNER and other departments are refused there regardless of UI.
export function DocumentReviewPanel({
  voucherId,
  reviewStatus,
  tasks,
  canDecide,
}: {
  voucherId: string;
  reviewStatus: string | null;
  tasks: DocumentReviewTask[];
  canDecide: boolean;
}) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  if (!reviewStatus && tasks.length === 0) return null;
  const open = tasks.find((t) => t.status === "open");
  const blocking = reviewStatus !== null && reviewStatus !== "approved";

  async function decide(taskId: string, decision: "approved" | "rejected") {
    setBusy(decision);
    setError(null);
    try {
      const res = await fetch(`/api/vouchers/${voucherId}/document-review/${taskId}/decision`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ decision, note: note.trim() || undefined }),
      });
      if (!res.ok) {
        const body = (await res.json().catch(() => ({}))) as { error?: string };
        setError(body.error ?? `HTTP ${res.status}`);
        return;
      }
      router.refresh();
    } finally {
      setBusy(null);
    }
  }

  return (
    <section
      data-testid="document-review-panel"
      className={`space-y-2 rounded-[var(--radius)] border p-4 text-sm ${blocking ? "border-amber-300 bg-amber-50" : "border-emerald-200 bg-emerald-50"}`}
    >
      <p className="flex items-center gap-2 font-medium" data-testid="document-review-status">
        <ShieldAlert className="size-4" /> {STATUS_LABEL[reviewStatus ?? ""] ?? reviewStatus ?? "—"}
      </p>
      <ul className="space-y-1 text-xs">
        {tasks.map((t) => (
          <li key={t.id}>
            <span className="font-mono">{t.id.slice(0, 8)}</span> · {t.documentType} · {t.status}
            {t.decisionNote ? ` · ${t.decisionNote}` : ""}
          </li>
        ))}
      </ul>
      {open && canDecide && (
        <div className="space-y-2 border-t pt-2">
          <input
            aria-label="Ghi chú duyệt"
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Ghi chú (tuỳ chọn)"
            className="w-full rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-sm"
          />
          <div className="flex gap-2">
            <Button size="sm" onClick={() => decide(open.id, "approved")} disabled={busy !== null}>
              {busy === "approved" ? <Loader2 className="size-4 animate-spin" /> : <CheckCircle2 className="size-4" />} Duyệt
            </Button>
            <Button size="sm" variant="outline" onClick={() => decide(open.id, "rejected")} disabled={busy !== null}>
              {busy === "rejected" ? <Loader2 className="size-4 animate-spin" /> : <XCircle className="size-4" />} Từ chối
            </Button>
          </div>
        </div>
      )}
      {error && <p className="text-xs text-red-700">{error}</p>}
    </section>
  );
}
