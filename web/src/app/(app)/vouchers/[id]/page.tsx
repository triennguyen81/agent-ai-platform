import Link from "next/link";
import { notFound } from "next/navigation";
import { AlertTriangle, ArrowLeft, Receipt, ShieldAlert, TrendingUp, Users } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { VoucherExtractForm } from "@/components/voucher-extract-form";

interface VoucherRecord {
  id: string;
  voucherType: string;
  status: string;
  documentNumber?: string | null;
  documentSeries?: string | null;
  documentDate?: string | null;
  sellerName?: string | null;
  sellerMst?: string | null;
  sellerAddress?: string | null;
  buyerName?: string | null;
  buyerMst?: string | null;
  buyerAddress?: string | null;
  paymentMethod?: string | null;
  subtotal?: number | null;
  vatRate?: number | null;
  vatAmount?: number | null;
  discountAmount?: number | null;
  totalAmount?: number | null;
  currency?: string | null;
  amountInWords?: string | null;
  tctVerified?: boolean;
  tctVerifiedAt?: string | null;
  sourceFilePath?: string | null;
  sourceFileName?: string | null;
  sourceMimeType?: string | null;
  rawOcrText?: string | null;
  ocrConfidence?: number | null;
  errorMessage?: string | null;
  extractedFieldsJson?: string | null;
  tags?: string[];
  createdAt: string;
  updatedAt: string;
}

// UC-ACC-001 §9.2: A/R context shape that /api/vouchers/upload injects into
// extracted_fields.ar_context after buyer_mst → customer lookup succeeds.
interface ArContextFound {
  status: "found";
  customer: {
    id: string;
    customerCode: string;
    name: string;
    creditLimit: number;
    currentAr: number;
    category: string;
  };
  current_balance_131: number;
  credit_limit: number;
  credit_used_percent: number;
  projected_balance_131: number;
  projected_credit_used_percent: number;
  aging: {
    current: number;
    bucket_0_30: number;
    bucket_31_60: number;
    bucket_61_90: number;
    over90: number;
  };
  overdue_amount: number;
  last_payment_date: string | null;
  payment_term_default: number;
  open_invoices: Array<{
    documentNumber: string;
    debit: number;
    dueDate: string | null;
    daysOverdue: number;
  }>;
  risk_flags: string[];
  requires_l2_approval: boolean;
}

interface ArContextNotFound {
  status: "customer_not_found" | "ar_lookup_failed";
  buyer_mst?: string;
}

type ArContext = ArContextFound | ArContextNotFound;

const RISK_FLAG_LABELS: Record<string, string> = {
  invoice_over_500m_requires_l2_approval: "Hoá đơn > 500 triệu → cần duyệt cấp 2 (Kế toán trưởng)",
  has_overdue_90d: "Khách có nợ quá hạn > 90 ngày",
  would_breach_credit_limit: "Vượt hạn mức tín dụng sau hoá đơn này",
  credit_used_gte_80pct: "Sử dụng ≥ 80% hạn mức tín dụng",
  has_overdue_amount: "Có công nợ quá hạn",
};

interface LineItem {
  id: string;
  ordinal: number;
  itemCode?: string | null;
  description: string;
  unit?: string | null;
  quantity?: number | null;
  unitPrice?: number | null;
  subtotal?: number | null;
  vatRate?: number | null;
  vatAmount?: number | null;
  totalAmount?: number | null;
}

interface JournalEntry {
  id: string;
  entryNo: number;
  debitAccount: string;
  creditAccount: string;
  amount: number;
  description?: string | null;
  confidence?: number | null;
  acceptedAt?: string | null;
  rejectedAt?: string | null;
}

interface VoucherDetail {
  voucher: VoucherRecord;
  lineItems: LineItem[];
  journalSuggestions: JournalEntry[];
}

const TYPE_LABEL: Record<string, string> = {
  invoice_vat: "Hoá đơn VAT",
  receipt: "Phiếu thu",
  payment: "Phiếu chi",
  bank_transfer: "Uỷ nhiệm chi",
  bank_statement: "Sao kê ngân hàng",
};

function fmtMoney(n: number | null | undefined, currency = "VND") {
  return n == null ? "—" : `${Number(n).toLocaleString("vi-VN")} ${currency}`;
}
function fmtDate(s: string | null | undefined) {
  if (!s) return "—";
  try {
    return new Date(s).toLocaleDateString("vi-VN");
  } catch {
    return s;
  }
}

function ArContextPanel({ ar, currency }: { ar: ArContextFound; currency: string }) {
  const willBreach = ar.risk_flags.includes("would_breach_credit_limit");
  const invoiceDelta = ar.projected_balance_131 - ar.current_balance_131;
  return (
    <section
      className={`space-y-4 rounded-[var(--radius)] border p-5 ${
        ar.requires_l2_approval ? "border-amber-300 bg-amber-50/40" : ""
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="font-serif text-lg">
            <Users className="mr-2 inline size-4" /> Công nợ khách hàng
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            <span className="font-mono">{ar.customer.customerCode}</span> · {ar.customer.name}
            <span className="ml-2 rounded-full border px-1.5 py-0.5 text-[10px]">
              {ar.customer.category === "phong_kham"
                ? "Phòng khám"
                : ar.customer.category === "benh_vien"
                  ? "Bệnh viện"
                  : ar.customer.category === "dai_ly"
                    ? "Đại lý"
                    : ar.customer.category}
            </span>
          </p>
        </div>
        {ar.requires_l2_approval && (
          <span className="inline-flex items-center gap-1 rounded-full bg-amber-600 px-3 py-1 text-xs font-medium text-white">
            <ShieldAlert className="size-3.5" /> Cần duyệt cấp 2
          </span>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Công nợ hiện tại (TK 131)</div>
          <div className="font-mono">{fmtMoney(ar.current_balance_131, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            <TrendingUp className="mr-0.5 inline size-3" /> Sau hoá đơn này
          </div>
          <div className={`font-mono font-bold ${willBreach ? "text-red-700" : ""}`}>
            {fmtMoney(ar.projected_balance_131, currency)}
            <span className="ml-1 text-xs font-normal text-[var(--color-muted-fg)]">
              (+{fmtMoney(invoiceDelta, currency)})
            </span>
          </div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Hạn mức tín dụng</div>
          <div className="font-mono">{fmtMoney(ar.credit_limit, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Dùng sau HĐ</div>
          <div
            className={`font-mono font-bold ${
              ar.projected_credit_used_percent >= 100
                ? "text-red-700"
                : ar.projected_credit_used_percent >= 80
                  ? "text-amber-700"
                  : "text-emerald-700"
            }`}
          >
            {ar.projected_credit_used_percent}%
          </div>
        </div>
      </div>

      <div className="space-y-1">
        <div className="text-xs text-[var(--color-muted-fg)]">
          Tuổi nợ (Aging) — tổng quá hạn: {fmtMoney(ar.overdue_amount, currency)}
        </div>
        <div className="grid grid-cols-5 gap-2 text-xs">
          <AgingCell label="Chưa đến hạn" amount={ar.aging.current} currency={currency} tone="emerald" />
          <AgingCell label="1-30 ngày" amount={ar.aging.bucket_0_30} currency={currency} tone="amber" />
          <AgingCell label="31-60 ngày" amount={ar.aging.bucket_31_60} currency={currency} tone="orange" />
          <AgingCell label="61-90 ngày" amount={ar.aging.bucket_61_90} currency={currency} tone="red" />
          <AgingCell label="> 90 ngày" amount={ar.aging.over90} currency={currency} tone="red-strong" />
        </div>
      </div>

      {ar.risk_flags.length > 0 && (
        <div className="space-y-1">
          <div className="text-xs text-[var(--color-muted-fg)]">Cảnh báo rủi ro</div>
          <ul className="space-y-1">
            {ar.risk_flags.map((flag) => (
              <li
                key={flag}
                className="flex items-start gap-2 rounded-[var(--radius)] border border-red-200 bg-red-50 px-3 py-1.5 text-xs text-red-800"
              >
                <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
                <span>{RISK_FLAG_LABELS[flag] ?? flag}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {ar.open_invoices.length > 0 && (
        <details className="text-xs">
          <summary className="cursor-pointer text-[var(--color-muted-fg)]">
            Hoá đơn chưa thu ({ar.open_invoices.length})
          </summary>
          <table className="mt-2 w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-2 py-1 text-left">Số HĐ</th>
                <th className="px-2 py-1 text-right">Số tiền</th>
                <th className="px-2 py-1 text-left">Hạn TT</th>
                <th className="px-2 py-1 text-right">Quá hạn</th>
              </tr>
            </thead>
            <tbody>
              {ar.open_invoices.slice(0, 10).map((inv) => (
                <tr key={inv.documentNumber} className="border-t">
                  <td className="px-2 py-1 font-mono">{inv.documentNumber}</td>
                  <td className="px-2 py-1 text-right font-mono">{fmtMoney(inv.debit, currency)}</td>
                  <td className="px-2 py-1">{fmtDate(inv.dueDate)}</td>
                  <td className={`px-2 py-1 text-right ${inv.daysOverdue > 0 ? "text-red-700" : ""}`}>
                    {inv.daysOverdue > 0 ? `${inv.daysOverdue} ngày` : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}

      <div className="flex flex-wrap gap-4 text-[11px] text-[var(--color-muted-fg)]">
        <span>
          Điều khoản TT mặc định: <span className="font-medium">Net {ar.payment_term_default}</span>
        </span>
        {ar.last_payment_date && (
          <span>
            Lần thanh toán gần nhất: <span className="font-medium">{fmtDate(ar.last_payment_date)}</span>
          </span>
        )}
      </div>
    </section>
  );
}

function AgingCell({
  label,
  amount,
  currency,
  tone,
}: {
  label: string;
  amount: number;
  currency: string;
  tone: "emerald" | "amber" | "orange" | "red" | "red-strong";
}) {
  const toneClass =
    tone === "emerald"
      ? "border-emerald-200 bg-emerald-50 text-emerald-800"
      : tone === "amber"
        ? "border-amber-200 bg-amber-50 text-amber-800"
        : tone === "orange"
          ? "border-orange-200 bg-orange-50 text-orange-800"
          : tone === "red"
            ? "border-red-200 bg-red-50 text-red-800"
            : "border-red-400 bg-red-100 text-red-900";
  return (
    <div className={`space-y-0.5 rounded-[var(--radius)] border p-2 ${toneClass}`}>
      <div className="text-[10px]">{label}</div>
      <div className="font-mono font-medium">{fmtMoney(amount, currency)}</div>
    </div>
  );
}

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function VoucherDetailPage({ params }: PageProps) {
  const { id } = await params;
  const token = await requireSession();
  const res = await gateway.getVoucher(token, id);
  if (!res.ok) {
    notFound();
  }
  const detail = (await res.json()) as VoucherDetail;
  const v = detail.voucher;
  const currency = v.currency ?? "VND";

  // UC-ACC-001 §5.5-5.7: parse A/R context enriched at upload time.
  let arContext: ArContext | null = null;
  if (v.extractedFieldsJson) {
    try {
      const parsed = JSON.parse(v.extractedFieldsJson) as { ar_context?: ArContext };
      arContext = parsed.ar_context ?? null;
    } catch {
      arContext = null;
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <Link
          href="/vouchers"
          className="inline-flex items-center gap-1 text-sm text-[var(--color-muted-fg)] hover:text-[var(--color-fg)]"
        >
          <ArrowLeft className="size-3.5" /> Tất cả chứng từ
        </Link>
      </div>

      <header className="space-y-2">
        <div className="flex items-center gap-2">
          <Receipt className="size-5 text-[var(--color-muted-fg)]" />
          <h1 className="font-serif text-2xl">
            {TYPE_LABEL[v.voucherType] ?? v.voucherType} ·{" "}
            {v.documentSeries ? `${v.documentSeries} · ` : ""}
            {v.documentNumber ?? "(chưa có số)"}
          </h1>
        </div>
        <div className="flex flex-wrap gap-2 text-xs text-[var(--color-muted-fg)]">
          <span>Ngày: {fmtDate(v.documentDate)}</span>
          <span>·</span>
          <span>Trạng thái: {v.status}</span>
          {v.ocrConfidence != null && (
            <>
              <span>·</span>
              <span>OCR confidence: {(v.ocrConfidence * 100).toFixed(0)}%</span>
            </>
          )}
          {v.sourceFileName && (
            <>
              <span>·</span>
              <span>File: {v.sourceFileName}</span>
            </>
          )}
        </div>
      </header>

      {v.errorMessage && (
        <div className="flex items-start gap-2 rounded-[var(--radius)] border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <div>
            <p className="font-medium">Lỗi xử lý</p>
            <p className="text-xs">{v.errorMessage}</p>
          </div>
        </div>
      )}

      {/* Uploaded file preview — images inline, PDFs as a download link.
          The /api/vouchers/{id}/preview route proxies through the gateway
          which either redirects to a signed MinIO URL (if the file is in
          S3) or streams from the legacy local upload volume. */}
      {v.sourceFilePath && (
        <section className="space-y-2 rounded-[var(--radius)] border p-5">
          <h2 className="font-serif text-lg">File gốc</h2>
          {v.sourceMimeType?.startsWith("image/") ? (
            <a
              href={`/api/vouchers/${v.id}/preview`}
              target="_blank"
              rel="noreferrer"
              className="block max-w-md"
            >
              <img
                src={`/api/vouchers/${v.id}/preview`}
                alt={v.sourceFileName ?? "Voucher"}
                className="rounded-[var(--radius)] border object-contain"
                style={{ maxHeight: 480 }}
              />
            </a>
          ) : (
            <a
              href={`/api/vouchers/${v.id}/preview`}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-2 text-sm text-[var(--color-primary)] hover:underline"
            >
              📄 {v.sourceFileName ?? "Download file"}
            </a>
          )}
        </section>
      )}

      {/* Fallback flow when OCR is unavailable or the voucher hasn't been
          extracted yet — user can paste raw text and trigger AI extraction. */}
      {(v.status === "ocr_unavailable" ||
        v.status === "processing" ||
        v.status === "ocr_complete") && (
        <VoucherExtractForm
          voucherId={v.id}
          voucherType={v.voucherType}
          initialRawText={v.rawOcrText}
        />
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="space-y-3 rounded-[var(--radius)] border p-5">
          <h2 className="font-serif text-lg">Bên bán</h2>
          <dl className="space-y-1 text-sm">
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">Tên</dt>
              <dd>{v.sellerName ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">MST</dt>
              <dd className="font-mono">{v.sellerMst ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">Địa chỉ</dt>
              <dd>{v.sellerAddress ?? "—"}</dd>
            </div>
          </dl>
        </section>

        <section className="space-y-3 rounded-[var(--radius)] border p-5">
          <h2 className="font-serif text-lg">Bên mua</h2>
          <dl className="space-y-1 text-sm">
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">Tên</dt>
              <dd>{v.buyerName ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">MST</dt>
              <dd className="font-mono">{v.buyerMst ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-[var(--color-muted-fg)]">Địa chỉ</dt>
              <dd>{v.buyerAddress ?? "—"}</dd>
            </div>
          </dl>
        </section>
      </div>

      {/* UC-ACC-001 §5.9 + BR-5: A/R context panel — full receivables snapshot
          of the buyer so the accountant sees credit-limit impact + risk flags
          before approving the AI-suggested journal. */}
      {arContext && arContext.status === "found" && (
        <ArContextPanel ar={arContext} currency={currency} />
      )}
      {arContext && arContext.status !== "found" && (
        <section className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
          <div className="flex items-start gap-2">
            <Users className="mt-0.5 size-4 shrink-0" />
            <div>
              <p className="font-medium">Khách hàng chưa có trong hệ thống</p>
              <p className="text-xs">
                MST <span className="font-mono">{arContext.buyer_mst ?? "?"}</span> không khớp với
                bất kỳ khách nào. Tạo hồ sơ mới hoặc kiểm tra lại MST trên hoá đơn.
              </p>
            </div>
          </div>
        </section>
      )}

      <section className="space-y-3 rounded-[var(--radius)] border p-5">
        <h2 className="font-serif text-lg">Số tiền</h2>
        <div className="grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
          <div>
            <div className="text-xs text-[var(--color-muted-fg)]">Cộng tiền hàng</div>
            <div className="font-mono">{fmtMoney(v.subtotal, currency)}</div>
          </div>
          <div>
            <div className="text-xs text-[var(--color-muted-fg)]">
              VAT {v.vatRate != null ? `(${v.vatRate}%)` : ""}
            </div>
            <div className="font-mono">{fmtMoney(v.vatAmount, currency)}</div>
          </div>
          <div>
            <div className="text-xs text-[var(--color-muted-fg)]">Chiết khấu</div>
            <div className="font-mono">{fmtMoney(v.discountAmount, currency)}</div>
          </div>
          <div>
            <div className="text-xs text-[var(--color-muted-fg)]">Tổng thanh toán</div>
            <div className="font-mono font-bold">{fmtMoney(v.totalAmount, currency)}</div>
          </div>
        </div>
        {v.amountInWords && (
          <p className="text-xs italic text-[var(--color-muted-fg)]">
            Bằng chữ: {v.amountInWords}
          </p>
        )}
        {v.paymentMethod && (
          <p className="text-xs text-[var(--color-muted-fg)]">
            Hình thức thanh toán: <span className="font-medium">{v.paymentMethod}</span>
          </p>
        )}
      </section>

      {detail.lineItems.length > 0 && (
        <section className="space-y-3 rounded-[var(--radius)] border p-5">
          <h2 className="font-serif text-lg">Chi tiết dòng hàng ({detail.lineItems.length})</h2>
          <div className="overflow-x-auto">
            <table className="w-full text-xs">
              <thead className="bg-[var(--color-muted)] text-[10px] uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-2 py-1.5 text-left">#</th>
                  <th className="px-2 py-1.5 text-left">Tên hàng</th>
                  <th className="px-2 py-1.5 text-left">ĐVT</th>
                  <th className="px-2 py-1.5 text-right">SL</th>
                  <th className="px-2 py-1.5 text-right">Đơn giá</th>
                  <th className="px-2 py-1.5 text-right">Thành tiền</th>
                  <th className="px-2 py-1.5 text-right">VAT</th>
                  <th className="px-2 py-1.5 text-right">Tổng</th>
                </tr>
              </thead>
              <tbody>
                {detail.lineItems.map((li) => (
                  <tr key={li.id} className="border-t">
                    <td className="px-2 py-1.5">{li.ordinal}</td>
                    <td className="px-2 py-1.5">{li.description}</td>
                    <td className="px-2 py-1.5">{li.unit ?? "—"}</td>
                    <td className="px-2 py-1.5 text-right font-mono">{li.quantity ?? "—"}</td>
                    <td className="px-2 py-1.5 text-right font-mono">{fmtMoney(li.unitPrice, currency)}</td>
                    <td className="px-2 py-1.5 text-right font-mono">{fmtMoney(li.subtotal, currency)}</td>
                    <td className="px-2 py-1.5 text-right font-mono">
                      {li.vatAmount != null
                        ? `${Number(li.vatAmount).toLocaleString("vi-VN")}${
                            li.vatRate != null ? ` (${li.vatRate}%)` : ""
                          }`
                        : "—"}
                    </td>
                    <td className="px-2 py-1.5 text-right font-mono font-medium">
                      {fmtMoney(li.totalAmount, currency)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      <section className="space-y-3 rounded-[var(--radius)] border p-5">
        <h2 className="font-serif text-lg">Bút toán đề xuất (TT200)</h2>
        {detail.journalSuggestions.length === 0 ? (
          <p className="text-sm text-[var(--color-muted-fg)]">
            Chưa có bút toán. AI sẽ suggest khi bạn quay lại trang sau vài giây, hoặc chạy lại từ trang upload.
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-[var(--color-muted)] text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
                <tr>
                  <th className="px-3 py-2 text-left">#</th>
                  <th className="px-3 py-2 text-left">Nợ</th>
                  <th className="px-3 py-2 text-left">Có</th>
                  <th className="px-3 py-2 text-right">Số tiền</th>
                  <th className="px-3 py-2 text-left">Diễn giải</th>
                  <th className="px-3 py-2 text-center">Confidence</th>
                  <th className="px-3 py-2 text-center">Trạng thái</th>
                </tr>
              </thead>
              <tbody>
                {detail.journalSuggestions.map((e) => (
                  <tr key={e.id} className="border-t">
                    <td className="px-3 py-2 font-mono">{e.entryNo}</td>
                    <td className="px-3 py-2 font-mono font-bold text-emerald-700">{e.debitAccount}</td>
                    <td className="px-3 py-2 font-mono font-bold text-red-700">{e.creditAccount}</td>
                    <td className="px-3 py-2 text-right font-mono">{fmtMoney(e.amount, currency)}</td>
                    <td className="px-3 py-2 text-xs">{e.description ?? "—"}</td>
                    <td className="px-3 py-2 text-center text-xs">
                      {e.confidence != null ? `${(e.confidence * 100).toFixed(0)}%` : "—"}
                    </td>
                    <td className="px-3 py-2 text-center text-xs">
                      {e.acceptedAt ? (
                        <span className="text-emerald-700">✓ Đã chấp nhận</span>
                      ) : e.rejectedAt ? (
                        <span className="text-red-700">✗ Đã từ chối</span>
                      ) : (
                        <span className="text-[var(--color-muted-fg)]">Chờ</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {v.rawOcrText && (
        <section className="space-y-2 rounded-[var(--radius)] border p-5">
          <h2 className="font-serif text-lg">Raw OCR text</h2>
          <pre className="max-h-80 overflow-auto rounded-[var(--radius)] bg-[var(--color-muted)]/40 p-3 text-xs whitespace-pre-wrap font-mono">
            {v.rawOcrText}
          </pre>
        </section>
      )}
    </div>
  );
}
