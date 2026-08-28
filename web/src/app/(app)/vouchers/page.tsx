import Link from "next/link";
import { redirect } from "next/navigation";
import { Receipt, Plus } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { Button } from "@/components/ui/button";

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
  processing: "Đang xử lý",
  ocr_complete: "OCR xong",
  extracted: "Đã trích xuất",
  journalized: "Đã định khoản",
  verified: "Đã xác thực",
  rejected: "Lỗi",
};

const TYPE_LABEL: Record<string, string> = {
  invoice_vat: "Hoá đơn VAT",
  receipt: "Phiếu thu",
  payment: "Phiếu chi",
  bank_transfer: "Uỷ nhiệm chi",
  bank_statement: "Sao kê NH",
};

function statusBadge(status: string): string {
  switch (status) {
    case "extracted":
    case "journalized":
    case "verified":
      return "bg-emerald-100 text-emerald-700";
    case "processing":
    case "ocr_complete":
      return "bg-amber-100 text-amber-700";
    case "rejected":
      return "bg-red-100 text-red-700";
    default:
      return "border text-[var(--color-muted-fg)]";
  }
}

function formatVnd(amount: number | null | undefined, currency?: string | null): string {
  if (amount == null) return "—";
  const cur = currency ?? "VND";
  return `${Number(amount).toLocaleString("vi-VN")} ${cur}`;
}

function formatDate(s: string | null | undefined): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

export default async function VouchersListPage() {
  const token = await requireSession();
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    const roles = me.roles ?? [];
    if (!roles.includes("super_admin") && !roles.includes("tenant_owner") && !roles.includes("admin")) {
      redirect("/chat");
    }
  }

  const res = await gateway.listVouchers(token);
  const body: VouchersListResponse = res.ok
    ? await res.json()
    : { items: [], total: 0 };

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-serif text-3xl">Chứng từ kế toán</h1>
          <p className="text-sm text-[var(--color-muted-fg)]">
            {body.total} chứng từ. AI tự OCR + trích xuất + đề xuất bút toán N/C theo TT200.
          </p>
        </div>
        <Button asChild>
          <Link href="/vouchers/upload">
            <Plus className="size-4" /> Thêm chứng từ
          </Link>
        </Button>
      </header>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-10 text-center">
          <Receipt className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="font-medium">Chưa có chứng từ nào</p>
          <p className="mt-1 text-sm text-[var(--color-muted-fg)]">
            Bấm "Thêm chứng từ" để upload ảnh hoá đơn / phiếu thu / chi.
          </p>
          <Button asChild className="mt-4">
            <Link href="/vouchers/upload">
              <Plus className="size-4" /> Thêm chứng từ đầu tiên
            </Link>
          </Button>
        </div>
      ) : (
        <div className="overflow-hidden rounded-[var(--radius)] border">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-4 py-2 text-left">Loại · Số CT</th>
                <th className="px-4 py-2 text-left">Ngày</th>
                <th className="px-4 py-2 text-left">Bên bán</th>
                <th className="px-4 py-2 text-right">Tổng tiền</th>
                <th className="px-4 py-2 text-right">VAT</th>
                <th className="px-4 py-2 text-center">Trạng thái</th>
              </tr>
            </thead>
            <tbody>
              {body.items.map((v) => (
                <tr key={v.id} className="border-t hover:bg-[var(--color-muted)]/30">
                  <td className="px-4 py-3">
                    <div className="text-xs text-[var(--color-muted-fg)]">
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
                        {v.tags.slice(0, 4).map((t) => (
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
                    <div className="text-[var(--color-muted-fg)]">MST: {v.sellerMst ?? "—"}</div>
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
      )}
    </div>
  );
}
