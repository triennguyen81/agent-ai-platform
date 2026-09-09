"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  AlertTriangle,
  CalendarClock,
  CheckCircle2,
  Circle,
  Clock,
  Loader2,
  Sparkles,
  Trash2,
} from "lucide-react";
import { Button } from "@/components/ui/button";

interface ChecklistItem {
  id: string;
  contractId: string;
  clauseId: string | null;
  title: string;
  description: string | null;
  dueDate: string | null;
  remindDaysBefore: number;
  assigneeEmail: string | null;
  status: "open" | "in_progress" | "done" | "skipped";
  completedAt: string | null;
  completedBy: string | null;
  createdAt: string;
  updatedAt: string;
}

type Filter = "all" | "open" | "due_soon" | "overdue" | "done";

interface Props {
  contractId: string;
  initialItems: ChecklistItem[];
}

function daysUntil(due: string | null): number | null {
  if (!due) return null;
  const target = new Date(due + "T00:00:00").getTime();
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return Math.ceil((target - today.getTime()) / (1000 * 60 * 60 * 24));
}

function statusFor(item: ChecklistItem): {
  label: string;
  cls: string;
  icon: React.ReactNode;
} {
  if (item.status === "done") {
    return {
      label: "Hoàn tất",
      cls: "border-emerald-300 bg-emerald-50",
      icon: <CheckCircle2 className="size-4 text-emerald-600" />,
    };
  }
  if (item.status === "skipped") {
    return {
      label: "Bỏ qua",
      cls: "border-gray-200 bg-gray-50 opacity-60",
      icon: <Circle className="size-4 text-gray-400" />,
    };
  }
  const days = daysUntil(item.dueDate);
  if (days != null && days < 0) {
    return {
      label: `Quá hạn ${-days} ngày`,
      cls: "border-red-300 bg-red-50",
      icon: <AlertTriangle className="size-4 text-red-600" />,
    };
  }
  if (days != null && days <= item.remindDaysBefore) {
    return {
      label: days === 0 ? "Đến hạn hôm nay" : `Còn ${days} ngày`,
      cls: "border-orange-300 bg-orange-50",
      icon: <Clock className="size-4 text-orange-600" />,
    };
  }
  if (item.status === "in_progress") {
    return {
      label: "Đang làm",
      cls: "border-blue-300 bg-blue-50",
      icon: <Loader2 className="size-4 text-blue-600" />,
    };
  }
  return {
    label: "Mở",
    cls: "border-[var(--color-border)] bg-[var(--color-bg)]",
    icon: <Circle className="size-4 text-[var(--color-muted-fg)]" />,
  };
}

function formatDate(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

export function ContractChecklist({ contractId, initialItems }: Props) {
  const router = useRouter();
  const [items, setItems] = useState<ChecklistItem[]>(initialItems);
  const [filter, setFilter] = useState<Filter>("all");
  const [generating, setGenerating] = useState(false);
  const [generateResult, setGenerateResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Track which items are currently being mutated (disable buttons)
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());

  const refresh = useCallback(async () => {
    try {
      const res = await fetch(`/api/contracts/${contractId}/checklist`);
      if (!res.ok) return;
      const body = (await res.json()) as { items: ChecklistItem[] };
      setItems(body.items ?? []);
    } catch {
      // silent
    }
  }, [contractId]);

  useEffect(() => {
    setItems(initialItems);
  }, [initialItems]);

  async function generate() {
    if (generating) return;
    setGenerating(true);
    setGenerateResult(null);
    setError(null);
    try {
      const res = await fetch(`/api/contracts/${contractId}/checklist/generate`, {
        method: "POST",
      });
      if (!res.ok) {
        // D6: never surface the raw upstream body — translate the
        // capability resolver's known denial statuses instead.
        if (res.status === 409) {
          setError("Nghiệp vụ này đang được gán cho nhiều Agent. Vui lòng liên hệ quản trị để cấu hình rõ ràng.");
        } else if (res.status === 403) {
          setError("Bạn chưa được cấp Agent hỗ trợ nghiệp vụ này. Liên hệ admin/tenant owner để được cấu hình.");
        } else {
          setError(`Đã có lỗi xảy ra (HTTP ${res.status}). Vui lòng thử lại.`);
        }
        return;
      }
      const body = (await res.json()) as { final_answer: string };
      setGenerateResult(body.final_answer);
      await refresh();
      router.refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
    } finally {
      setGenerating(false);
    }
  }

  async function updateItem(itemId: string, patch: Record<string, unknown>) {
    setPendingIds((prev) => new Set(prev).add(itemId));
    try {
      const res = await fetch(`/api/checklist/${itemId}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch),
      });
      if (!res.ok) return;
      const updated = (await res.json()) as ChecklistItem;
      setItems((prev) => prev.map((i) => (i.id === itemId ? updated : i)));
    } finally {
      setPendingIds((prev) => {
        const next = new Set(prev);
        next.delete(itemId);
        return next;
      });
    }
  }

  async function deleteItem(itemId: string) {
    if (!confirm("Xoá item này?")) return;
    setPendingIds((prev) => new Set(prev).add(itemId));
    try {
      const res = await fetch(`/api/checklist/${itemId}`, { method: "DELETE" });
      if (res.ok) {
        setItems((prev) => prev.filter((i) => i.id !== itemId));
      }
    } finally {
      setPendingIds((prev) => {
        const next = new Set(prev);
        next.delete(itemId);
        return next;
      });
    }
  }

  function toggleStatus(item: ChecklistItem) {
    const next = item.status === "done" ? "open" : "done";
    void updateItem(item.id, { status: next });
  }

  const filtered = items.filter((item) => {
    if (filter === "all") return true;
    if (filter === "done") return item.status === "done";
    if (filter === "open") return item.status === "open" || item.status === "in_progress";
    const days = daysUntil(item.dueDate);
    if (days == null) return false;
    if (filter === "due_soon") {
      return (
        item.status !== "done" &&
        item.status !== "skipped" &&
        days >= 0 &&
        days <= item.remindDaysBefore
      );
    }
    if (filter === "overdue") {
      return item.status !== "done" && item.status !== "skipped" && days < 0;
    }
    return true;
  });

  const counts = {
    total: items.length,
    open: items.filter((i) => i.status === "open" || i.status === "in_progress").length,
    overdue: items.filter((i) => {
      if (i.status === "done" || i.status === "skipped") return false;
      const d = daysUntil(i.dueDate);
      return d != null && d < 0;
    }).length,
    dueSoon: items.filter((i) => {
      if (i.status === "done" || i.status === "skipped") return false;
      const d = daysUntil(i.dueDate);
      return d != null && d >= 0 && d <= i.remindDaysBefore;
    }).length,
    done: items.filter((i) => i.status === "done").length,
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <h2 className="font-serif text-xl">Checklist ({counts.total})</h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            {counts.overdue} quá hạn · {counts.dueSoon} sắp đến hạn · {counts.done} hoàn tất
          </p>
        </div>
        <Button onClick={generate} disabled={generating}>
          {generating ? <Loader2 className="size-4 animate-spin" /> : <Sparkles className="size-4" />}
          {items.length === 0 ? "AI sinh checklist" : "AI sinh lại (replace)"}
        </Button>
      </div>

      {error && (
        <p className="rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          {error}
        </p>
      )}

      {generateResult && (
        <div className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50 p-3 text-sm">
          <p className="font-medium text-emerald-700">✓ AI đã hoàn tất</p>
          <p className="mt-1 whitespace-pre-wrap text-xs text-emerald-700">{generateResult}</p>
        </div>
      )}

      {items.length > 0 && (
        <div className="flex flex-wrap gap-1 rounded-full border border-[var(--color-border)] bg-[var(--color-bg)] p-1 text-xs">
          {(
            [
              ["all", `Tất cả (${counts.total})`],
              ["open", `Mở (${counts.open})`],
              ["due_soon", `Sắp hạn (${counts.dueSoon})`],
              ["overdue", `Quá hạn (${counts.overdue})`],
              ["done", `Hoàn tất (${counts.done})`],
            ] as [Filter, string][]
          ).map(([key, label]) => (
            <button
              key={key}
              type="button"
              onClick={() => setFilter(key)}
              className={`flex-1 rounded-full px-3 py-1.5 transition ${
                filter === key
                  ? "bg-[var(--color-primary)] text-[var(--color-primary-fg)]"
                  : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
              }`}
            >
              {label}
            </button>
          ))}
        </div>
      )}

      {filtered.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-8 text-center">
          <CalendarClock className="mx-auto mb-3 size-8 text-[var(--color-muted-fg)]" />
          <p className="text-sm text-[var(--color-muted-fg)]">
            {items.length === 0
              ? "Chưa có checklist. Bấm 'AI sinh checklist' để bắt đầu."
              : "Không có item phù hợp với filter."}
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {filtered.map((item) => {
            const status = statusFor(item);
            const pending = pendingIds.has(item.id);
            const done = item.status === "done";
            return (
              <li
                key={item.id}
                className={`flex items-start gap-3 rounded-[var(--radius)] border p-3 transition ${status.cls}`}
              >
                <button
                  type="button"
                  onClick={() => toggleStatus(item)}
                  disabled={pending}
                  className="mt-0.5 shrink-0"
                  aria-label={done ? "Đánh dấu chưa làm" : "Đánh dấu hoàn tất"}
                >
                  {status.icon}
                </button>
                <div className="min-w-0 flex-1 space-y-1">
                  <p
                    className={`text-sm font-medium ${done ? "text-[var(--color-muted-fg)] line-through" : ""}`}
                  >
                    {item.title}
                  </p>
                  {item.description && (
                    <p className="text-xs text-[var(--color-muted-fg)]">{item.description}</p>
                  )}
                  <div className="flex flex-wrap items-center gap-2 text-[10px] text-[var(--color-muted-fg)]">
                    {item.dueDate && (
                      <span>
                        Hạn: <strong>{formatDate(item.dueDate)}</strong>
                      </span>
                    )}
                    {item.assigneeEmail && <span>· @{item.assigneeEmail}</span>}
                    {item.remindDaysBefore !== 7 && (
                      <span>· Nhắc trước {item.remindDaysBefore}d</span>
                    )}
                    <span>· {status.label}</span>
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <select
                    value={item.status}
                    onChange={(e) =>
                      void updateItem(item.id, { status: e.target.value })
                    }
                    disabled={pending}
                    className="rounded border border-[var(--color-border)] bg-[var(--color-bg)] px-2 py-1 text-xs"
                  >
                    <option value="open">Mở</option>
                    <option value="in_progress">Đang làm</option>
                    <option value="done">Hoàn tất</option>
                    <option value="skipped">Bỏ qua</option>
                  </select>
                  <button
                    type="button"
                    onClick={() => deleteItem(item.id)}
                    disabled={pending}
                    className="rounded p-1 text-[var(--color-muted-fg)] hover:bg-red-50 hover:text-red-600 disabled:opacity-50"
                    aria-label="Xoá"
                  >
                    <Trash2 className="size-3.5" />
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
