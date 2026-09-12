"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Filter, Search } from "lucide-react";
import { Button } from "@/components/ui/button";

interface VouchersToolbarProps {
  currentSort: string;
  currentPageSize: number;
  currentQ: string;
  currentStatus: string | undefined;
  currentSellerMst: string | undefined;
  currentDocumentDateFrom: string | undefined;
  currentDocumentDateTo: string | undefined;
  sortOptions: ReadonlyArray<{ value: string; label: string }>;
  pageSizes: ReadonlyArray<number>;
}

export function VouchersToolbar({
  currentSort,
  currentPageSize,
  currentQ,
  currentStatus,
  currentSellerMst,
  currentDocumentDateFrom,
  currentDocumentDateTo,
  sortOptions,
  pageSizes,
}: VouchersToolbarProps) {
  const router = useRouter();
  const hasAdvancedFilter = Boolean(
    currentSellerMst || currentDocumentDateFrom || currentDocumentDateTo,
  );
  const [advancedOpen, setAdvancedOpen] = useState(hasAdvancedFilter);

  const buildUrl = (overrides: Record<string, string | undefined>): string => {
    const p = new URLSearchParams();
    const merged: Record<string, string | undefined> = {
      status: currentStatus,
      q: currentQ || undefined,
      sellerMst: currentSellerMst,
      documentDateFrom: currentDocumentDateFrom,
      documentDateTo: currentDocumentDateTo,
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

  const hasAnyFilter =
    Boolean(currentQ) ||
    Boolean(currentStatus) ||
    Boolean(currentSellerMst) ||
    Boolean(currentDocumentDateFrom) ||
    Boolean(currentDocumentDateTo);

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-[var(--radius)] border bg-[var(--color-bg)] p-3">
        <form action="/vouchers" method="GET" className="flex flex-1 flex-wrap items-center gap-2">
          {currentStatus && <input type="hidden" name="status" value={currentStatus} />}
          {currentSort !== "created_desc" && <input type="hidden" name="sort" value={currentSort} />}
          <input type="hidden" name="pageSize" value={String(currentPageSize)} />
          {currentSellerMst && <input type="hidden" name="sellerMst" value={currentSellerMst} />}
          {currentDocumentDateFrom && (
            <input type="hidden" name="documentDateFrom" value={currentDocumentDateFrom} />
          )}
          {currentDocumentDateTo && (
            <input type="hidden" name="documentDateTo" value={currentDocumentDateTo} />
          )}
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
          <button
            type="button"
            onClick={() => setAdvancedOpen((prev) => !prev)}
            className={`inline-flex items-center gap-1 rounded-[var(--radius)] border px-2 py-1 text-xs transition ${
              advancedOpen || hasAdvancedFilter
                ? "border-[var(--color-primary)] text-[var(--color-primary)]"
                : "text-[var(--color-muted-fg)] hover:bg-[var(--color-muted)]"
            }`}
          >
            <Filter className="size-3" />
            Lọc nâng cao
            {hasAdvancedFilter && (
              <span className="ml-1 rounded-full bg-[var(--color-primary)]/10 px-1 text-[10px]">
                đang lọc
              </span>
            )}
          </button>
          {hasAnyFilter && (
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

      {advancedOpen && (
        <form
          action="/vouchers"
          method="GET"
          className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-3"
        >
          {/* Preserve existing filters through hidden inputs */}
          {currentStatus && <input type="hidden" name="status" value={currentStatus} />}
          {currentQ && <input type="hidden" name="q" value={currentQ} />}
          {currentSort !== "created_desc" && (
            <input type="hidden" name="sort" value={currentSort} />
          )}
          <input type="hidden" name="pageSize" value={String(currentPageSize)} />

          <div className="grid gap-3 sm:grid-cols-3">
            <label className="flex flex-col gap-1 text-xs text-[var(--color-muted-fg)]">
              MST nhà cung cấp
              <input
                name="sellerMst"
                defaultValue={currentSellerMst ?? ""}
                placeholder="0123456789"
                inputMode="numeric"
                pattern="[0-9\-]*"
                className="rounded-[var(--radius)] border px-2 py-1.5 text-sm"
              />
            </label>
            <label className="flex flex-col gap-1 text-xs text-[var(--color-muted-fg)]">
              Từ ngày HĐ
              <input
                type="date"
                name="documentDateFrom"
                defaultValue={currentDocumentDateFrom ?? ""}
                className="rounded-[var(--radius)] border px-2 py-1.5 text-sm"
              />
            </label>
            <label className="flex flex-col gap-1 text-xs text-[var(--color-muted-fg)]">
              Đến ngày HĐ
              <input
                type="date"
                name="documentDateTo"
                defaultValue={currentDocumentDateTo ?? ""}
                className="rounded-[var(--radius)] border px-2 py-1.5 text-sm"
              />
            </label>
          </div>
          <div className="mt-3 flex items-center gap-2">
            <Button type="submit" size="sm">Áp dụng</Button>
            {hasAdvancedFilter && (
              <Link
                href={buildUrl({
                  sellerMst: undefined,
                  documentDateFrom: undefined,
                  documentDateTo: undefined,
                })}
                className="text-xs text-[var(--color-muted-fg)] hover:underline"
              >
                Xoá lọc nâng cao
              </Link>
            )}
            <span className="ml-auto text-[11px] text-[var(--color-muted-fg)]">
              Dùng cho tra cứu hồ sơ theo NCC + khoảng thời gian (bank / kiểm toán)
            </span>
          </div>
        </form>
      )}
    </div>
  );
}
