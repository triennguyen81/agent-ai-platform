"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { ArrowDown, ArrowUp, ArrowUpDown } from "lucide-react";

interface SortableThProps {
  /** URL slug used in ?sort=. Toggles between `${sortKey}_desc` and `${sortKey}_asc`. */
  sortKey: string;
  /** Current active sort value from the page (e.g. "amount_desc"). */
  currentSort: string;
  align?: "left" | "right" | "center";
  children: React.ReactNode;
}

/**
 * Table header cell that clicks → sort by this column.
 *
 * First click → `${sortKey}_desc`. Second click → `${sortKey}_asc`.
 * Third click → falls back to the default `created_desc`.
 */
export function SortableTh({
  sortKey,
  currentSort,
  align = "left",
  children,
}: SortableThProps) {
  const router = useRouter();
  const searchParams = useSearchParams();

  const isActive = currentSort.startsWith(sortKey + "_");
  const isDesc = currentSort === `${sortKey}_desc`;
  const isAsc = currentSort === `${sortKey}_asc`;

  const next = isDesc ? `${sortKey}_asc` : isAsc ? "created_desc" : `${sortKey}_desc`;

  const handleClick = () => {
    const params = new URLSearchParams(searchParams.toString());
    if (next === "created_desc") {
      params.delete("sort");
    } else {
      params.set("sort", next);
    }
    params.set("page", "1");
    router.push(`/vouchers?${params.toString()}`);
  };

  const alignClass =
    align === "right" ? "justify-end" : align === "center" ? "justify-center" : "justify-start";
  const thAlignClass =
    align === "right" ? "text-right" : align === "center" ? "text-center" : "text-left";

  const Icon = isDesc ? ArrowDown : isAsc ? ArrowUp : ArrowUpDown;

  return (
    <th className={`px-4 py-2 ${thAlignClass}`}>
      <button
        type="button"
        onClick={handleClick}
        className={`inline-flex w-full items-center gap-1 text-xs uppercase tracking-wider transition ${alignClass} ${
          isActive
            ? "text-[var(--color-primary)]"
            : "text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        }`}
      >
        <span>{children}</span>
        <Icon className={`size-3 ${isActive ? "" : "opacity-40"}`} />
      </button>
    </th>
  );
}
