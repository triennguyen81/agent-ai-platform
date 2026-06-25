"use client";

import Link from "next/link";
import { useState } from "react";
import { AlertTriangle, CheckCheck, Clock, FileText, Inbox } from "lucide-react";
import { Button } from "@/components/ui/button";

export interface NotificationRecord {
  id: string;
  userId: string;
  kind: string;
  refType: string | null;
  refId: string | null;
  title: string;
  body: string | null;
  readAt: string | null;
  createdAt: string;
}

interface Props {
  initialItems: NotificationRecord[];
}

function iconFor(kind: string) {
  if (kind === "checklist_overdue") return <AlertTriangle className="size-4 text-red-600" />;
  if (kind === "checklist_due_soon") return <Clock className="size-4 text-orange-600" />;
  if (kind === "contract_due") return <FileText className="size-4 text-amber-600" />;
  return <Inbox className="size-4 text-[var(--color-muted-fg)]" />;
}

function formatRelative(iso: string): string {
  const then = new Date(iso).getTime();
  const now = Date.now();
  const diffMs = now - then;
  const min = Math.round(diffMs / 60_000);
  if (min < 1) return "vừa xong";
  if (min < 60) return `${min} phút trước`;
  const hour = Math.round(min / 60);
  if (hour < 24) return `${hour} giờ trước`;
  const day = Math.round(hour / 24);
  return `${day} ngày trước`;
}

function detailHref(item: NotificationRecord): string | null {
  if (item.refType === "checklist_item" && item.refId) {
    return `/contracts?focus=${item.refId}`;
  }
  if (item.refType === "contract" && item.refId) {
    return `/contracts/${item.refId}`;
  }
  return null;
}

export function NotificationsList({ initialItems }: Props) {
  const [items, setItems] = useState<NotificationRecord[]>(initialItems);
  const [pending, setPending] = useState(false);

  async function markOne(id: string) {
    setItems((prev) =>
      prev.map((i) => (i.id === id ? { ...i, readAt: i.readAt ?? new Date().toISOString() } : i)),
    );
    try {
      await fetch(`/api/notifications/${id}/read`, { method: "PATCH" });
    } catch {
      // optimistic — leave state as-is
    }
  }

  async function markAll() {
    if (pending) return;
    setPending(true);
    try {
      const res = await fetch("/api/notifications/mark-all-read", { method: "POST" });
      if (res.ok) {
        const stamp = new Date().toISOString();
        setItems((prev) => prev.map((i) => (i.readAt ? i : { ...i, readAt: stamp })));
      }
    } finally {
      setPending(false);
    }
  }

  const unreadCount = items.filter((i) => !i.readAt).length;

  if (items.length === 0) {
    return (
      <div className="rounded-[var(--radius)] border border-dashed p-12 text-center">
        <Inbox className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
        <p className="text-sm text-[var(--color-muted-fg)]">
          Chưa có thông báo nào. Cron sẽ chạy mỗi sáng 7:00 quét checklist sắp đến hạn.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-xs text-[var(--color-muted-fg)]">
          {unreadCount} chưa đọc · {items.length} tổng cộng
        </p>
        {unreadCount > 0 && (
          <Button variant="ghost" size="sm" onClick={markAll} disabled={pending}>
            <CheckCheck className="size-4" /> Đánh dấu tất cả đã đọc
          </Button>
        )}
      </div>

      <ul className="space-y-2">
        {items.map((item) => {
          const href = detailHref(item);
          const unread = !item.readAt;
          const inner = (
            <div
              className={`flex items-start gap-3 rounded-[var(--radius)] border p-3 transition ${
                unread
                  ? "border-[var(--color-primary)]/40 bg-[var(--color-primary)]/5"
                  : "border-[var(--color-border)] bg-[var(--color-bg)] opacity-70"
              }`}
            >
              <div className="mt-0.5 shrink-0">{iconFor(item.kind)}</div>
              <div className="min-w-0 flex-1">
                <p className={`text-sm ${unread ? "font-medium" : ""}`}>{item.title}</p>
                {item.body && (
                  <p className="mt-1 text-xs text-[var(--color-muted-fg)]">{item.body}</p>
                )}
                <p className="mt-1 text-[10px] text-[var(--color-muted-fg)]">
                  {formatRelative(item.createdAt)}
                </p>
              </div>
              {unread && (
                <span className="mt-1 size-2 shrink-0 rounded-full bg-[var(--color-primary)]" />
              )}
            </div>
          );

          return (
            <li key={item.id}>
              {href ? (
                <Link href={href} onClick={() => unread && void markOne(item.id)}>
                  {inner}
                </Link>
              ) : (
                <button
                  type="button"
                  onClick={() => unread && void markOne(item.id)}
                  className="w-full text-left"
                >
                  {inner}
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
