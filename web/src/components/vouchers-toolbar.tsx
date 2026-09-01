"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { Search } from "lucide-react";
import { Button } from "@/components/ui/button";

interface VouchersToolbarProps {
  currentSort: string;
  currentPageSize: number;
  currentQ: string;
  currentStatus: string | undefined;
  sortOptions: ReadonlyArray<{ value: string; label: string }>;
  pageSizes: ReadonlyArray<number>;
}

export function VouchersToolbar({
  currentSort,
  currentPageSize,
  currentQ,
  currentStatus,
  sortOptions,
  pageSizes,
}: VouchersToolbarProps) {
  const router = useRouter();

  const buildUrl = (overrides: Record<string, string | undefined>): string => {
    const p = new URLSearchParams();
    const merged: Record<string, string | undefined> = {
      status: currentStatus,
      q: currentQ || undefined,
      sort: currentSort,
      pageSize: String(currentPageSize),
      page: "1",
      ...overrides,
    };
    for (const [k, v] of Object.entries(merged)) {
      if (v !== undefined && v !== "" && v !== null) p.set(k, String(v));
    }
    const qs = p.toString();
    return `/vouchers${qs ? `?${qs}` : ""}`;
  };

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-3">
      <form action="/vouchers" method="GET" className="flex flex-1 items-center gap-2">
        {currentStatus && <input type="hidden" name="status" value={currentStatus} />}
        {currentSort !== "created_desc" && <input type="hidden" name="sort" value={currentSort} />}
        <input type="hidden" name="pageSize" value={String(currentPageSize)} />
        <div className="relative max-w-md flex-1">
          <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-muted-fg)]" />
          <input
            name="q"
            defaultValue={currentQ}
            placeholder="Tìm số HĐ, tên bên bán, MST..."
            className="w-full rounded-[var(--radius)] border py-1.5 pl-7 pr-3 text-sm"
          />
        </div>
        <Button type="submit" size="sm" variant="outline">Tìm</Button>
        {(currentQ || currentStatus) && (
          <Link
            href="/vouchers"
            className="text-xs text-[var(--color-muted-fg)] hover:underline"
          >
            Xoá lọc
          </Link>
        )}
      </form>

      <div className="flex items-center gap-3 text-xs text-[var(--color-muted-fg)]">
        <label className="flex items-center gap-1.5">
          <span>Sắp xếp:</span>
          <select
            defaultValue={currentSort}
            className="rounded-[var(--radius)] border px-2 py-1 text-xs"
            onChange={(e) => router.push(buildUrl({ sort: e.target.value }))}
          >
            {sortOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>{opt.label}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1.5">
          <span>Trang:</span>
          <select
            defaultValue={currentPageSize}
            className="rounded-[var(--radius)] border px-2 py-1 text-xs"
            onChange={(e) => router.push(buildUrl({ pageSize: e.target.value }))}
          >
            {pageSizes.map((n) => (
              <option key={n} value={n}>{n}/trang</option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}
