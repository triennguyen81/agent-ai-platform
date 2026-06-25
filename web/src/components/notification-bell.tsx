"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Bell } from "lucide-react";

const POLL_MS = 60_000;

export function NotificationBell() {
  const [count, setCount] = useState<number>(0);

  useEffect(() => {
    let cancelled = false;

    async function fetchCount() {
      try {
        const res = await fetch("/api/notifications/unread-count");
        if (!res.ok) return;
        const body = (await res.json()) as { count?: number };
        if (!cancelled) setCount(body.count ?? 0);
      } catch {
        // silent — bell stays at last known count
      }
    }

    void fetchCount();
    const timer = setInterval(fetchCount, POLL_MS);

    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  return (
    <Link
      href="/notifications"
      aria-label={`Thông báo (${count} chưa đọc)`}
      className="relative inline-flex h-9 w-9 items-center justify-center rounded-md text-[var(--color-muted-fg)] hover:bg-[var(--color-muted)] hover:text-[var(--color-fg)]"
    >
      <Bell className="size-4" />
      {count > 0 && (
        <span className="absolute -right-0.5 -top-0.5 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-red-600 px-1 text-[10px] font-semibold text-white">
          {count > 99 ? "99+" : count}
        </span>
      )}
    </Link>
  );
}
