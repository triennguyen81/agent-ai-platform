import Link from "next/link";
import { redirect } from "next/navigation";
import { ChevronLeft, ChevronRight, Plus, Receipt } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { VouchersToolbar } from "@/components/vouchers-toolbar";
import { SortableTh } from "@/components/sortable-th";

interface VoucherRecord {
  id: string;
  voucherType: string;
  status: string;
  documentNumber?: string | null;
  documentSeries?: string | null;
  documentDate?: string | null;
  sellerName?: string | null;
  sellerMst?: string | null;
  buyerName?: string | null;
  buyerMst?: string | null;
  totalAmount?: number | null;
  vatAmount?: number | null;
  vatRate?: number | null;
  currency?: string | null;
  tags?: string[];
  createdAt: string;
}

interface VouchersListResponse {
  items: VoucherRecord[];
  total: number;
}

const STATUS_LABEL: Record<string, string> = {
  uploading: "Đang tải lên",
  processing: "Đang OCR",
  ocr_complete: "OCR xong",
  ocr_unavailable: "OCR lỗi",
  extracted: "Đã trích xuất",
  journalized: "Đã định khoản",
  verified: "Đã xác thực",
  rejected: "Từ chối",
};

const TYPE_LABEL: Record<string, string> = {
  invoice_vat: "Hoá đơn VAT",
  receipt: "Phiếu thu",
  payment: "Phiếu chi",
  bank_transfer: "Uỷ nhiệm chi",
  bank_statement: "Sao kê NH",
};

const STATUS_TABS: Array<{ value: string | "all"; label: string; tone: string }> = [
  { value: "all", label: "Tất cả", tone: "border-[var(--color-primary)] text-[var(--color-primary)]" },
  { value: "extracted", label: "Đã trích xuất", tone: "border-emerald-400 text-emerald-700" },
  { value: "verified", label: "Đã xác thực", tone: "border-emerald-600 text-emerald-800" },
  { value: "journalized", label: "Đã định khoản", tone: "border-emerald-600 text-emerald-800" },
  { value: "processing", label: "Đang OCR", tone: "border-amber-400 text-amber-700" },
  { value: "ocr_complete", label: "Chờ AI trích", tone: "border-amber-400 text-amber-700" },
  { value: "ocr_unavailable", label: "OCR lỗi", tone: "border-red-400 text-red-700" },
  { value: "rejected", label: "Từ chối", tone: "border-red-400 text-red-700" },
];

const PAGE_SIZES = [25, 50, 100] as const;
const SORT_OPTIONS = [
  { value: "created_desc", label: "Mới upload trước" },
  { value: "created_asc", label: "Cũ nhất trước" },
  { value: "date_desc", label: "Ngày HĐ mới nhất" },
  { value: "date_asc", label: "Ngày HĐ cũ nhất" },
  { value: "amount_desc", label: "Số tiền cao nhất" },
  { value: "amount_asc", label: "Số tiền thấp nhất" },
  { value: "number_desc", label: "Số CT Z→A" },
  { value: "number_asc", label: "Số CT A→Z" },
  { value: "seller_desc", label: "Bên bán Z→A" },
  { value: "seller_asc", label: "Bên bán A→Z" },
  { value: "status_desc", label: "Trạng thái Z→A" },
  { value: "status_asc", label: "Trạng thái A→Z" },
] as const;

function statusBadge(status: string): string {
  switch (status) {
    case "extracted":
    case "journalized":
    case "verified":
      return "bg-emerald-100 text-emerald-700";
    case "processing":
    case "ocr_complete":
    case "uploading":
      return "bg-amber-100 text-amber-700";
    case "rejected":
    case "ocr_unavailable":
      return "bg-red-100 text-red-700";
    default:
      return "border text-[var(--color-muted-fg)]";
  }
}

function formatVnd(amount: number | null | undefined, currency?: string | null): string {
  if (amount == null) return "—";
  return `${Number(amount).toLocaleString("vi-VN")} ${currency ?? "VND"}`;
}

function formatDate(s: string | null | undefined): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

interface PageProps {
  searchParams: Promise<{
    status?: string;
    q?: string;
    page?: string;
    pageSize?: string;
    sort?: string;
  }>;
}

export default async function VouchersListPage({ searchParams }: PageProps) {
  const params = await searchParams;
  const token = await requireSession();
  // Kế toán viên (role=user) can also manage vouchers — that's their
  // primary daily job. Gateway policy is 'user' (all authenticated
  // tenant members) so we don't need a role check here.

  const status = params.status && params.status !== "all" ? params.status : undefined;
  const q = params.q?.trim() || undefined;
  const page = Math.max(1, parseInt(params.page ?? "1", 10) || 1);
  const pageSize = PAGE_SIZES.includes(parseInt(params.pageSize ?? "25", 10) as 25 | 50 | 100)
    ? (parseInt(params.pageSize!, 10) as 25 | 50 | 100)
    : 25;
  const sort = SORT_OPTIONS.find((s) => s.value === params.sort)?.value ?? "created_desc";
  const offset = (page - 1) * pageSize;

  // Gateway supports voucherType/status/q + limit/offset. Sort is applied
  // client-side over the returned page — good enough for MVP, will move
  // to server-side ORDER BY once volumes grow.
  const res = await gateway.listVouchers(token, { status, q, limit: pageSize, offset });
  const body: VouchersListResponse = res.ok ? await res.json() : { items: [], total: 0 };

  const sorted = [...body.items].sort((a, b) => {
    switch (sort) {
      case "created_asc":
        return a.createdAt.localeCompare(b.createdAt);
      case "date_desc":
        return (b.documentDate ?? "").localeCompare(a.documentDate ?? "");
      case "date_asc":
        return (a.documentDate ?? "").localeCompare(b.documentDate ?? "");
      case "amount_desc":
        return (b.totalAmount ?? 0) - (a.totalAmount ?? 0);
      case "amount_asc":
        return (a.totalAmount ?? 0) - (b.totalAmount ?? 0);
      case "number_desc":
        return (b.documentNumber ?? "").localeCompare(a.documentNumber ?? "");
      case "number_asc":
        return (a.documentNumber ?? "").localeCompare(b.documentNumber ?? "");
      case "seller_desc":
        return (b.sellerName ?? "").localeCompare(a.sellerName ?? "", "vi");
      case "seller_asc":
        return (a.sellerName ?? "").localeCompare(b.sellerName ?? "", "vi");
      case "status_desc":
        return b.status.localeCompare(a.status);
      case "status_asc":
        return a.status.localeCompare(b.status);
      case "created_desc":
      default:
        return b.createdAt.localeCompare(a.createdAt);
    }
  });

  const totalPages = Math.max(1, Math.ceil(body.total / pageSize));

  const buildQs = (overrides: Record<string, string | undefined>) => {
    const p = new URLSearchParams();
    const merged = { status: params.status, q, sort, pageSize: String(pageSize), page: String(page), ...overrides };
    for (const [k, v] of Object.entries(merged)) {
      if (v !== undefined && v !== "" && v !== null) p.set(k, String(v));
    }
    const qs = p.toString();
    return qs ? `?${qs}` : "";
  };

  const activeTab = params.status ?? "all";

  return (
    <div className="space-y-5">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">
            <Receipt className="mr-2 inline size-6" /> Chứng từ kế toán
          </h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            {body.total} chứng từ · AI tự OCR + trích xuất + đề xuất bút toán N/C theo TT200
          </p>
        </div>
        <Button asChild>
          <Link href="/vouchers/upload">
            <Plus className="size-4" /> Thêm chứng từ
          </Link>
        </Button>
      </header>

      {/* Status group tabs — mirror gateway ?status filter */}
      <div className="flex flex-wrap gap-2">
        {STATUS_TABS.map((tab) => {
          const isActive = activeTab === tab.value;
          return (
            <Link
              key={tab.value}
              href={`/vouchers${buildQs({ status: tab.value === "all" ? undefined : tab.value, page: "1" })}`}
              className={`inline-flex items-center gap-1 rounded-full border px-3 py-1 text-xs transition ${
                isActive ? tab.tone + " bg-[var(--color-muted)]/40 font-medium" : "text-[var(--color-muted-fg)] hover:bg-[var(--color-muted)]"
              }`}
            >
              {tab.label}
            </Link>
          );
        })}
      </div>

      <VouchersToolbar
        currentSort={sort}
        currentPageSize={pageSize}
        currentQ={q ?? ""}
        currentStatus={params.status}
        sortOptions={SORT_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
        pageSizes={PAGE_SIZES}
      />

      {sorted.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-10 text-center">
          <Receipt className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="font-medium">Không có chứng từ khớp bộ lọc</p>
          <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
            {q || params.status ? "Thử xoá bộ lọc hoặc từ khoá khác." : 'Bấm "Thêm chứng từ" để upload ảnh hoá đơn.'}
          </p>
          <Button asChild className="mt-4">
            <Link href="/vouchers/upload">
              <Plus className="size-4" /> Thêm chứng từ
            </Link>
          </Button>
        </div>
      ) : (
        <>
          <div className="overflow-hidden rounded-[var(--radius)] border">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)]">
                <tr>
                  <SortableTh sortKey="number" currentSort={sort}>Loại · Số CT</SortableTh>
                  <SortableTh sortKey="date" currentSort={sort}>Ngày HĐ</SortableTh>
                  <SortableTh sortKey="seller" currentSort={sort}>Bên bán / Bên mua</SortableTh>
                  <SortableTh sortKey="amount" currentSort={sort} align="right">Tổng tiền</SortableTh>
                  <th className="px-4 py-2 text-right text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">VAT</th>
                  <SortableTh sortKey="status" currentSort={sort} align="center">Trạng thái</SortableTh>
                </tr>
              </thead>
              <tbody>
                {sorted.map((v) => (
                  <tr key={v.id} className="border-t hover:bg-[var(--color-muted)]/30">
                    <td className="px-4 py-3">
                      <div className="text-[10px] uppercase tracking-wider text-[var(--color-muted-fg)]">
                        {TYPE_LABEL[v.voucherType] ?? v.voucherType}
                      </div>
                      <Link
                        href={`/vouchers/${v.id}`}
                        className="font-medium text-[var(--color-primary)] hover:underline"
                      >
                        {v.documentSeries ? `${v.documentSeries} · ` : ""}
                        {v.documentNumber ?? "(chưa có)"}
                      </Link>
                      {v.tags && v.tags.length > 0 && (
                        <div className="mt-1 flex flex-wrap gap-1">
                          {v.tags.slice(0, 3).map((t) => (
                            <span
                              key={t}
                              className="rounded-full border px-1.5 py-0.5 text-[10px] text-[var(--color-muted-fg)]"
                            >
                              {t}
                            </span>
                          ))}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs">{formatDate(v.documentDate)}</td>
                    <td className="px-4 py-3 text-xs">
                      <div className="font-medium">{v.sellerName ?? "—"}</div>
                      <div className="text-[10px] text-[var(--color-muted-fg)]">
                        MST: {v.sellerMst ?? "—"}
                      </div>
                      {v.buyerName && (
                        <div className="mt-0.5 text-[10px] text-[var(--color-muted-fg)]">
                          → {v.buyerName} ({v.buyerMst ?? "—"})
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right font-mono text-xs">
                      {formatVnd(v.totalAmount, v.currency)}
                    </td>
                    <td className="px-4 py-3 text-right font-mono text-xs">
                      {v.vatAmount != null
                        ? `${Number(v.vatAmount).toLocaleString("vi-VN")}${
                            v.vatRate != null ? ` (${v.vatRate}%)` : ""
                          }`
                        : "—"}
                    </td>
                    <td className="px-4 py-3 text-center">
                      <span
                        className={`inline-block rounded-full px-2 py-0.5 text-xs ${statusBadge(v.status)}`}
                      >
                        {STATUS_LABEL[v.status] ?? v.status}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          <div className="flex items-center justify-between text-xs text-[var(--color-muted-fg)]">
            <div>
              Hiển thị <b>{offset + 1}</b>–<b>{Math.min(offset + sorted.length, body.total)}</b> trên{" "}
              <b>{body.total}</b> chứng từ
            </div>
            <div className="flex items-center gap-1">
              <Button
                asChild
                variant="outline"
                size="sm"
                disabled={page <= 1}
                className={page <= 1 ? "pointer-events-none opacity-50" : ""}
              >
                <Link href={`/vouchers${buildQs({ page: String(page - 1) })}`}>
                  <ChevronLeft className="size-3.5" /> Trước
                </Link>
              </Button>
              <span className="mx-2">
                Trang {page} / {totalPages}
              </span>
              <Button
                asChild
                variant="outline"
                size="sm"
                disabled={page >= totalPages}
                className={page >= totalPages ? "pointer-events-none opacity-50" : ""}
              >
                <Link href={`/vouchers${buildQs({ page: String(page + 1) })}`}>
                  Sau <ChevronRight className="size-3.5" />
                </Link>
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
