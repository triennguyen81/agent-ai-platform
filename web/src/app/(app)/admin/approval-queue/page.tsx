import Link from "next/link";
import { AlertTriangle, ClipboardCheck, Inbox, ShieldAlert } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { ReviewButtons } from "./review-buttons";

// Sprint 5 HITL — /admin/approval-queue.
// Danh sách voucher risk cao đang chờ duyệt. Kế toán trưởng approve/reject
// mỗi item. Approve → banner "Đã duyệt" — kế toán viên có thể vào Accept.
// Reject → voucher không được apply.

interface ApprovalSummary {
  id: string;
  voucherId: string;
  voucherType: string;
  documentNumber: string | null;
  totalAmount: number | null;
  currency: string;
  triggerReasons: string[];
  status: string;
  createdAt: string;
  sellerName: string | null;
  buyerName: string | null;
  documentDate: string | null;
  submitterEmail: string | null;
}

const TRIGGER_LABELS: Record<string, string> = {
  invoice_over_500m_requires_l2_approval: "Hoá đơn > 500M (AR)",
  expense_over_500m_requires_l2_approval: "Chi phí > 500M (AP)",
  expense_over_30m_cash_payment: "Chi TM > 30M (vi phạm TT78)",
  has_overdue_90d: "Nợ quá hạn > 90 ngày",
  would_breach_credit_limit: "Vượt hạn mức tín dụng",
  has_overdue_payable: "Có phải trả NCC quá hạn",
  first_transaction_with_supplier: "NCC mới — cần verify KYC",
};

const VOUCHER_TYPE_LABELS: Record<string, string> = {
  invoice_vat_out: "Hoá đơn bán",
  invoice_vat_in: "Hoá đơn mua",
  invoice_vat: "Hoá đơn VAT",
  receipt: "Phiếu thu",
  payment: "Phiếu chi TM",
  bank_transfer: "UNC",
  expense_claim: "Hoàn ứng NV",
};

function fmtMoney(n: number | null, currency = "VND"): string {
  if (n === null) return "—";
  return `${n.toLocaleString("vi-VN")} ${currency}`;
}

function fmtDate(s: string | null): string {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

function fmtRelative(s: string): string {
  const now = Date.now();
  const then = new Date(s).getTime();
  const diffMin = Math.floor((now - then) / 60000);
  if (diffMin < 60) return `${diffMin} phút trước`;
  if (diffMin < 1440) return `${Math.floor(diffMin / 60)} giờ trước`;
  return `${Math.floor(diffMin / 1440)} ngày trước`;
}

export default async function ApprovalQueuePage() {
  const token = await requireSession();
  const res = await gateway.listApprovalQueue(token);
  const body = res.ok
    ? ((await res.json()) as { items: ApprovalSummary[]; total: number })
    : { items: [], total: 0 };

  return (
    <div className="space-y-6">
      <header className="space-y-2">
        <h1 className="font-serif text-3xl">
          <ShieldAlert className="mr-2 inline size-6 text-amber-600" /> Duyệt cấp 2
        </h1>
        <p className="text-sm text-[var(--color-muted-fg)]">
          Voucher risk cao chờ kế toán trưởng duyệt trước khi kế toán viên có thể áp vào ledger.
          Chỉ voucher trigger <code>requires_l2_approval</code> lọt vào đây.
        </p>
      </header>

      <section className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Đang chờ duyệt</div>
          <div className="mt-1 font-mono text-2xl font-bold text-amber-700">{body.items.length}</div>
        </div>
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Tổng giá trị</div>
          <div className="mt-1 font-mono text-2xl font-bold">
            {fmtMoney(
              body.items.reduce((sum, i) => sum + (i.totalAmount ?? 0), 0),
              "VND",
            )}
          </div>
        </div>
        <div className="rounded-[var(--radius)] border p-4">
          <div className="text-xs text-[var(--color-muted-fg)]">Rủi ro cao nhất</div>
          <div className="mt-1 text-sm">
            {body.items.some((i) => i.triggerReasons.includes("expense_over_30m_cash_payment"))
              ? "TT78 vi phạm"
              : body.items.some((i) => (i.totalAmount ?? 0) >= 500_000_000)
                ? "> 500M"
                : "Bình thường"}
          </div>
        </div>
      </section>

      {body.items.length === 0 ? (
        <div className="rounded-[var(--radius)] border border-dashed p-12 text-center">
          <Inbox className="mx-auto mb-3 size-10 text-[var(--color-muted-fg)]" />
          <p className="text-sm text-[var(--color-muted-fg)]">
            Không có voucher nào đang chờ duyệt.
          </p>
        </div>
      ) : (
        <div className="space-y-3">
          {body.items.map((item) => (
            <article
              key={item.id}
              className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-4"
            >
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0 flex-1 space-y-2">
                  {/* Header */}
                  <div className="flex flex-wrap items-center gap-2 text-sm">
                    <Link
                      href={`/vouchers/${item.voucherId}`}
                      className="font-mono font-medium text-[var(--color-primary)] hover:underline"
                    >
                      {item.documentNumber ?? "(no doc#)"}
                    </Link>
                    <span className="rounded-full border px-2 py-0.5 text-[10px]">
                      {VOUCHER_TYPE_LABELS[item.voucherType] ?? item.voucherType}
                    </span>
                    <span className="text-xs text-[var(--color-muted-fg)]">
                      · {fmtDate(item.documentDate)} · {fmtRelative(item.createdAt)}
                    </span>
                  </div>

                  {/* Parties */}
                  <div className="text-sm">
                    {item.sellerName && (
                      <span>
                        <span className="text-[var(--color-muted-fg)]">Bên bán:</span>{" "}
                        <span className="font-medium">{item.sellerName}</span>
                      </span>
                    )}
                    {item.sellerName && item.buyerName && <span className="mx-2">·</span>}
                    {item.buyerName && (
                      <span>
                        <span className="text-[var(--color-muted-fg)]">Bên mua:</span>{" "}
                        <span className="font-medium">{item.buyerName}</span>
                      </span>
                    )}
                  </div>

                  {/* Amount */}
                  <div className="font-mono text-lg font-bold">
                    {fmtMoney(item.totalAmount, item.currency)}
                  </div>

                  {/* Triggers */}
                  <div className="flex flex-wrap gap-1.5">
                    {item.triggerReasons.map((flag) => (
                      <span
                        key={flag}
                        className="inline-flex items-center gap-1 rounded-full border border-red-200 bg-red-50 px-2 py-0.5 text-[11px] text-red-800"
                      >
                        <AlertTriangle className="size-3" />
                        {TRIGGER_LABELS[flag] ?? flag}
                      </span>
                    ))}
                  </div>

                  {/* Submitter */}
                  {item.submitterEmail && (
                    <div className="text-[11px] text-[var(--color-muted-fg)]">
                      Do <span className="font-mono">{item.submitterEmail}</span> upload
                    </div>
                  )}
                </div>

                {/* Review actions */}
                <ReviewButtons id={item.id} />
              </div>
            </article>
          ))}
        </div>
      )}

      <p className="text-[11px] italic text-[var(--color-muted-fg)]">
        <ClipboardCheck className="mr-1 inline size-3" />
        <b>Note:</b> Duyệt tại đây không tự động apply — kế toán viên vẫn phải bấm Accept trên
        voucher detail. Reject → voucher không được apply, cần xử lý ngoại lệ.
      </p>
    </div>
  );
}
