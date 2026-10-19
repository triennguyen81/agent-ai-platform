import Link from "next/link";
import { notFound } from "next/navigation";
import { AlertTriangle, ArrowLeft, Banknote, CheckCircle2, ClipboardCheck, FileText, Package, Receipt, ShieldAlert, TrendingUp, Truck, UserCircle2, Users } from "lucide-react";
import { gateway } from "@/lib/api";
import { requireSession } from "@/lib/auth";
import { VoucherExtractForm } from "@/components/voucher-extract-form";
import { CashSettlementPanel, type SettlementContext } from "@/components/cash-settlement-panel";
import { VoucherTypeReview } from "@/components/voucher-type-review";
import { DocumentReviewPanel, type DocumentReviewTask } from "@/components/document-review-panel";
import { ExtractionFailureBanner } from "@/components/extraction-failure-banner";
import { BankStatementPanel, type CompanyAccountOption, type PartyOption } from "@/components/bank-statement-panel";
import { importNoteFrom, type StatementTransactionView } from "@/lib/bank-statement";
import { parseClassification, voucherTypeLabel } from "@/lib/voucher-type";
import {
  buildPartyCards,
  overlayLiveAp,
  overlayLiveAr,
  pendingBalanceDelta,
  snapshotPartyId,
  type LivePayables,
  type LiveReceivables,
} from "@/lib/voucher-view";
import { AcceptExpenseButton } from "@/components/accept-expense-button";
import { ExpenseClaimPaymentPanel } from "@/components/expense-claim-payment-panel";
import { ExpenseClaimItemsPanel } from "@/components/expense-claim-items-panel";
import { splitJournal } from "@/lib/expense-claim-payment";
import { AcceptInvoiceButton } from "@/components/accept-invoice-button";
import { TaxVerificationPanel } from "@/components/tax-verification-panel";
import { RejectReceiptMatchButton } from "@/components/reject-receipt-match-button";
import { RejectExpenseClaimButton } from "@/components/reject-expense-claim-button";
import { GrantExpenseExceptionButton } from "@/components/grant-expense-exception-button";
import { DepartmentReviewExpenseButton } from "@/components/department-review-expense-button";
import { AccountingReviewExpenseButton } from "@/components/accounting-review-expense-button";

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

// UC-ACC-001 §5.6: 3-way match context stored by /api/vouchers/upload in
// extracted_fields.match_context.
interface MatchContextFound {
  status: "found";
  quality: "exact" | "partial" | string;
  sales_order: {
    orderNumber: string;
    orderDate: string;
    status: string;
    totalAmount: number;
    paymentTermsDays: number;
    salespersonCode: string | null;
    notes: string | null;
  };
  delivery_note: {
    dnNumber: string;
    dnDate: string;
    warehouseFrom: string;
    deliveredBy: string | null;
    receivedBy: string | null;
    status: string;
  } | null;
  contract: {
    contractNumber: string;
    contractTitle: string;
    contractValue: number;
    milestoneNo: number;
    milestoneDesc: string;
    milestoneAmount: number;
    milestoneStatus: string;
  } | null;
}

interface MatchContextNone {
  status: "no_match";
  document_number?: string;
}

type MatchContext = MatchContextFound | MatchContextNone;

// Receipt matching context — deterministic FIFO built in gateway when
// voucher_type=receipt AND payer has open A/R.
interface ReceiptApplication {
  document_number: string;
  document_date: string;
  due_date: string | null;
  invoice_remaining_before: number;
  applied: number;
  invoice_remaining_after: number;
}

interface ReceiptMatchingFound {
  status:
    | "matched_full"
    | "matched_partial"
    | "matched_multiple"
    | "matched_with_discount"
    | "overpaid"
    | "no_open_invoices";
  receipt_amount: number;
  total_applied: number;
  remaining_overpayment: number;
  discount: number | null;
  discount_reason: string | null;
  applications: ReceiptApplication[];
  message: string;
}

type ReceiptMatching = ReceiptMatchingFound;

// Luồng 02 — persisted Customer/AR/Contract-installment match proposal
// (GET /vouchers/{id}/receipt-match), independent of the extracted_fields
// snapshot above (that's the raw extraction; this is the reviewed outcome).
interface ReceiptMatchResult {
  id: string;
  voucherId: string;
  customerId: string | null;
  customerMatchConfidence: number;
  customerMatchEvidence: string | null;
  contractId: string | null;
  installmentId: string | null;
  receivableReference: string | null;
  outcome:
    | "FULL_MATCH"
    | "PARTIAL_PAYMENT"
    | "OVERPAYMENT"
    | "ADVANCE_PAYMENT"
    | "UNKNOWN_PAYER"
    | "CUSTOMER_NO_OPEN_RECEIVABLE"
    | "REVERSED_TRANSACTION"
    | "FOREIGN_CURRENCY";
  receiptAmount: number;
  matchedAmount: number;
  unallocatedAmount: number;
  currency: string;
  reviewStatus: "proposed" | "confirmed" | "rejected";
  reviewedBy: string | null;
  reviewedAt: string | null;
}

// Configurable document processing provenance, written by the extract job
// into extracted_fields.document_processing (agent / version / config source).
interface DocumentProcessingProvenance {
  config_source: string;
  agent_name?: string | null;
  agent_version?: number | null;
  provider?: string | null;
  model?: string | null;
  schema_valid?: boolean | null;
  unsupported_document_type?: boolean;
}

function documentProcessingFrom(json?: string | null): DocumentProcessingProvenance | null {
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as { document_processing?: DocumentProcessingProvenance };
    return parsed.document_processing ?? null;
  } catch {
    return null;
  }
}

// Luồng 03 — persisted expense-claim outcome classification (GET
// /vouchers/{id}/expense-claim-result), independent of the extracted_fields
// expense_claim_matching snapshot below (that's the raw enrichment; this is
// the reviewed outcome — duplicate/policy/advance reconciliation).
interface ExpenseClaimResult {
  id: string;
  voucherId: string;
  employeeId: string | null;
  requestedAmount: number;
  approvedAmount: number;
  blockedAmount: number;
  cappedAmount: number;
  nonDeductibleAmount: number;
  vatAmount: number;
  vatDeductibleAmount: number;
  advanceAmount: number;
  payableAmount: number;
  currency: string;
  duplicateFlagsJson: string;
  policyFlagsJson: string;
  resolvedVendorsJson: string;
  missingDocuments: string | null;
  outcome:
    | "APPROVED"
    | "PARTIALLY_APPROVED"
    | "RETURN_FOR_DOCUMENTS"
    | "WAITING_EXCEPTION_APPROVAL"
    | "REJECTED";
  // R14 — 3-stage sequence: proposed -> dept_reviewed -> accounting_reviewed
  // -> confirmed (or rejected from any of the first three).
  reviewStatus: "proposed" | "dept_reviewed" | "accounting_reviewed" | "confirmed" | "rejected";
  reviewedBy: string | null;
  reviewedAt: string | null;
}

// UC3 AP-side enrichment: extracted_fields.ap_context / payment_matching /
// expense_claim_matching populated by VoucherExtractJobRunner.
interface ApContextFound {
  status: "found";
  supplier: {
    id: string;
    supplierCode: string;
    name: string;
    mst: string | null;
    country: string;
    currency: string;
    paymentTermsDays: number;
    currentAp: number;
  };
  current_balance_331: number;
  projected_balance_331: number;
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
    credit: number;
    dueDate: string | null;
    daysOverdue: number;
  }>;
  risk_flags: string[];
  requires_l2_approval: boolean;
}

interface ApContextNotFound {
  status: "supplier_not_found" | "ap_lookup_failed";
  supplier_mst?: string;
}

type ApContext = ApContextFound | ApContextNotFound;

interface PaymentApplication {
  document_number: string;
  document_date: string;
  due_date: string | null;
  invoice_remaining_before: number;
  applied: number;
  invoice_remaining_after: number;
}

interface PaymentMatchingFound {
  status:
    | "matched_full"
    | "matched_partial"
    | "matched_multiple"
    | "overpaid"
    | "no_open_payables";
  payment_amount: number;
  total_applied: number;
  remaining_overpayment: number;
  applications: PaymentApplication[];
  message: string;
}

type PaymentMatching = PaymentMatchingFound;

interface ExpenseClaimItem {
  category: string;
  description: string;
  amount: number;
  receipt_attached: string;
  journal_account: string;
  tt96_flagged: boolean;
}

interface ExpenseClaimJournalLine {
  debit_account: string;
  category: string;
  amount: number;
  tt96_flagged: boolean;
}

interface ExpenseClaimMatchingFound {
  status: "found";
  employee: {
    id: string;
    employeeCode: string;
    fullName: string;
    department: string | null;
    monthlyExpenseBudget: number;
    ytdExpense: number;
    outstandingAdvance: number;
  };
  total_claim: number;
  advance_taken: number;
  advance_covered: number;
  remaining_to_reimburse: number;
  annual_ceiling: number;
  budget_exceeded: boolean;
  default_debit_account: string;
  journal_breakdown: ExpenseClaimJournalLine[];
  items: ExpenseClaimItem[];
  has_tt96_category: boolean;
  suggested_journal_hint: string;
}

interface ExpenseClaimMatchingNone {
  status: "employee_not_found";
  employee_code?: string;
}

type ExpenseClaimMatching = ExpenseClaimMatchingFound | ExpenseClaimMatchingNone;

// UC3 §5.6 (Sprint 4): PO 3-way match context — invoice_vat_in → PO → GRN.
interface PoMatchContextFound {
  status: "found";
  quality: "exact" | "partial" | string;
  purchase_order: {
    poNumber: string;
    poDate: string;
    supplierCode: string;
    status: string;
    currency: string;
    exchangeRate: number;
    subtotalVnd: number;
    totalAmount: number | null;
    incoterm: string | null;
    paymentTerms: string | null;
    paymentTermsDays: number;
    deliveryNoteRef: string | null;
    matchedInvoiceNumber: string | null;
    notes: string | null;
  };
  goods_received_note: {
    grnNumber: string;
    grnDate: string;
    warehouseTo: string | null;
    deliveredBy: string | null;
    receivedBy: string | null;
    status: string;
  } | null;
}

interface PoMatchContextNone {
  status: "no_match";
  document_number?: string;
}

type PoMatchContext = PoMatchContextFound | PoMatchContextNone;

const RISK_FLAG_LABELS: Record<string, string> = {
  // AR side (UC1)
  invoice_over_500m_requires_l2_approval: "Hoá đơn > 500 triệu → cần duyệt cấp 2 (Kế toán trưởng)",
  has_overdue_90d: "Khách/NCC có nợ quá hạn > 90 ngày",
  would_breach_credit_limit: "Vượt hạn mức tín dụng sau hoá đơn này",
  credit_used_gte_80pct: "Sử dụng ≥ 80% hạn mức tín dụng",
  has_overdue_amount: "Có công nợ quá hạn",
  // AP side (UC3)
  expense_over_30m_cash_payment: "Chi tiền mặt > 30M → KHÔNG được khấu trừ VAT (TT78 điều 6)",
  expense_over_500m_requires_l2_approval: "Chi > 500 triệu → cần duyệt cấp 2 (Kế toán trưởng)",
  has_overdue_payable: "Có phải trả NCC quá hạn",
  first_transaction_with_supplier: "Giao dịch đầu tiên với NCC — cần verify hồ sơ KYC",
  would_exceed_payment_terms: "Vượt điều khoản thanh toán NCC",
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
  rejectReason?: string | null;
  /** suggestion (AI/manual, advisory) | posting | reversal (authoritative). */
  source?: string | null;
}

interface VoucherDetail {
  voucher: VoucherRecord;
  lineItems: LineItem[];
  journalSuggestions: JournalEntry[];
}


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

function ReceiptMatchingPanel({
  matching,
  currency,
  voucherId,
  voucherStatus,
}: {
  matching: ReceiptMatchingFound;
  currency: string;
  voucherId: string;
  voucherStatus: string;
}) {
  const tone =
    matching.status === "matched_with_discount"
      ? "border-emerald-300 bg-emerald-50/40"
      : matching.status === "overpaid"
        ? "border-amber-300 bg-amber-50/40"
        : matching.status === "no_open_invoices"
          ? "border-blue-200 bg-blue-50/40"
          : "border-emerald-300 bg-emerald-50/40";
  return (
    <section className={`space-y-3 rounded-[var(--radius)] border p-5 ${tone}`}>
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-serif text-lg">
            <ClipboardCheck className="mr-2 inline size-4" /> Áp thu vào công nợ
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">{matching.message}</p>
        </div>
        <div className="text-right">
          <div className="text-xs text-[var(--color-muted-fg)]">Số tiền thu</div>
          <div className="font-mono text-lg font-bold">
            {fmtMoney(matching.receipt_amount, currency)}
          </div>
        </div>
      </div>

      {matching.applications.length > 0 && (
        <div className="overflow-hidden rounded-[var(--radius)] border bg-[var(--color-bg)]">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]/60 text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Số HĐ</th>
                <th className="px-3 py-2 text-left">Hạn TT</th>
                <th className="px-3 py-2 text-right">Còn nợ trước</th>
                <th className="px-3 py-2 text-right">Áp thu</th>
                <th className="px-3 py-2 text-right">Còn nợ sau</th>
              </tr>
            </thead>
            <tbody>
              {matching.applications.map((app) => (
                <tr key={app.document_number} className="border-t">
                  <td className="px-3 py-2 font-mono">{app.document_number}</td>
                  <td className="px-3 py-2 text-xs">{fmtDate(app.due_date)}</td>
                  <td className="px-3 py-2 text-right font-mono text-xs">
                    {fmtMoney(app.invoice_remaining_before, currency)}
                  </td>
                  <td className="px-3 py-2 text-right font-mono text-emerald-700">
                    {fmtMoney(app.applied, currency)}
                  </td>
                  <td
                    className={`px-3 py-2 text-right font-mono text-xs ${
                      app.invoice_remaining_after === 0 ? "text-emerald-700 font-bold" : ""
                    }`}
                  >
                    {fmtMoney(app.invoice_remaining_after, currency)}
                    {app.invoice_remaining_after === 0 && " ✓"}
                  </td>
                </tr>
              ))}
              <tr className="border-t bg-[var(--color-muted)]/30">
                <td colSpan={3} className="px-3 py-2 text-right text-xs font-medium">
                  Tổng áp thu:
                </td>
                <td className="px-3 py-2 text-right font-mono font-bold text-emerald-800">
                  {fmtMoney(matching.total_applied, currency)}
                </td>
                <td className="px-3 py-2" />
              </tr>
            </tbody>
          </table>
        </div>
      )}

      {matching.discount !== null && matching.discount > 0 && (
        <div className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          <b>Chiết khấu thanh toán sớm:</b> {fmtMoney(matching.discount, currency)}
          <div className="mt-0.5 text-xs">
            Bút toán bổ sung: <span className="font-mono">Nợ 5211 / Có 131</span>{" "}
            {fmtMoney(matching.discount, currency)}
          </div>
        </div>
      )}

      {matching.remaining_overpayment > 0 && (
        <div className="rounded-[var(--radius)] border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <b>Thu dư:</b> {fmtMoney(matching.remaining_overpayment, currency)} — treo vào
          TK 3387 (khách trả trước) hoặc trả lại.
        </div>
      )}

      {/* Approval moved to the "Thu tiền · Đối trừ công nợ" panel above —
          this FIFO table (built at extraction time) is informational only;
          posting uses the server-side match with ADMIN selection. */}
      {voucherStatus !== "verified" && (
        <p className="border-t pt-3 text-[11px] italic text-[var(--color-muted-fg)]">
          Bảng trên là gợi ý FIFO lúc trích xuất (tham khảo). Duyệt &amp; ghi nhận tại khung
          “Thu tiền · Đối trừ công nợ”.
        </p>
      )}
      {voucherStatus === "verified" && (
        <div className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          ✓ Đã áp thu vào công nợ. Xem lịch sử trong <a href={arContextLink(matching)} className="underline">/customers</a>.
        </div>
      )}
    </section>
  );
}

function arContextLink(_matching: ReceiptMatchingFound): string {
  return "/customers";
}

function MatchContextPanel({ match, currency }: { match: MatchContextFound; currency: string }) {
  const so = match.sales_order;
  const dn = match.delivery_note;
  const ctr = match.contract;
  const isExact = match.quality === "exact";

  return (
    <section
      className={`space-y-3 rounded-[var(--radius)] border p-5 ${
        isExact ? "border-emerald-300 bg-emerald-50/40" : "border-amber-300 bg-amber-50/40"
      }`}
    >
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-serif text-lg">
            <ClipboardCheck className="mr-2 inline size-4" /> Đối chiếu 3 chiều
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            Hoá đơn ↔ Đơn hàng ↔ Phiếu giao {ctr ? "↔ Hợp đồng" : ""}
          </p>
        </div>
        <span
          className={`inline-flex items-center gap-1 rounded-full px-3 py-1 text-xs font-medium ${
            isExact ? "bg-emerald-600 text-white" : "bg-amber-500 text-white"
          }`}
        >
          {isExact ? <CheckCircle2 className="size-3.5" /> : <AlertTriangle className="size-3.5" />}
          {isExact ? "Khớp đầy đủ" : "Khớp một phần"}
        </span>
      </div>

      <div className="grid gap-3 md:grid-cols-3">
        <div className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-3 text-sm">
          <div className="mb-1 flex items-center gap-1 text-xs font-medium text-[var(--color-muted-fg)]">
            <FileText className="size-3.5" /> Đơn hàng (SO)
          </div>
          <div className="font-mono font-bold">{so.orderNumber}</div>
          <div className="mt-1 space-y-0.5 text-xs">
            <div>Ngày: {fmtDate(so.orderDate)}</div>
            <div>Trạng thái: <span className="font-medium">{so.status}</span></div>
            <div>Giá trị: <span className="font-mono">{fmtMoney(so.totalAmount, currency)}</span></div>
            <div>Điều khoản: <span className="font-medium">Net {so.paymentTermsDays}</span></div>
            {so.salespersonCode && <div>KDV: <span className="font-mono">{so.salespersonCode}</span></div>}
          </div>
          {so.notes && (
            <div className="mt-2 border-t pt-2 text-[11px] italic text-[var(--color-muted-fg)]">
              {so.notes}
            </div>
          )}
        </div>

        <div className={`rounded-[var(--radius)] border p-3 text-sm ${dn ? "bg-[var(--color-bg)]" : "bg-amber-100/50"}`}>
          <div className="mb-1 flex items-center gap-1 text-xs font-medium text-[var(--color-muted-fg)]">
            <Package className="size-3.5" /> Phiếu xuất kho (DN)
          </div>
          {dn ? (
            <>
              <div className="font-mono font-bold">{dn.dnNumber}</div>
              <div className="mt-1 space-y-0.5 text-xs">
                <div>Ngày: {fmtDate(dn.dnDate)}</div>
                <div>Kho: <span className="font-medium">{dn.warehouseFrom}</span></div>
                <div>Trạng thái: <span className="font-medium">{dn.status}</span></div>
                {dn.deliveredBy && <div>Giao: {dn.deliveredBy}</div>}
                {dn.receivedBy && <div>Nhận: {dn.receivedBy}</div>}
              </div>
            </>
          ) : (
            <div className="text-xs text-amber-800">
              <b>Chưa có phiếu xuất kho.</b> Cần kiểm tra: hàng đã giao chưa? Xuất kho bằng chứng từ khác?
            </div>
          )}
        </div>

        <div className={`rounded-[var(--radius)] border p-3 text-sm ${ctr ? "bg-[var(--color-bg)]" : "bg-gray-50"}`}>
          <div className="mb-1 flex items-center gap-1 text-xs font-medium text-[var(--color-muted-fg)]">
            <FileText className="size-3.5" /> Hợp đồng (nếu có)
          </div>
          {ctr ? (
            <>
              <div className="font-mono font-bold">{ctr.contractNumber}</div>
              <div className="mt-1 space-y-0.5 text-xs">
                <div className="truncate" title={ctr.contractTitle}>{ctr.contractTitle}</div>
                <div>Giá trị HĐ: <span className="font-mono">{fmtMoney(ctr.contractValue, currency)}</span></div>
                <div className="mt-1.5 rounded border bg-[var(--color-muted)]/30 p-1.5">
                  <div className="text-[10px] uppercase tracking-wider text-[var(--color-muted-fg)]">
                    Milestone {ctr.milestoneNo}
                  </div>
                  <div className="text-xs">{ctr.milestoneDesc}</div>
                  <div className="mt-0.5 flex justify-between text-[11px]">
                    <span className="font-mono">{fmtMoney(ctr.milestoneAmount, currency)}</span>
                    <span className="font-medium">
                      {ctr.milestoneStatus === "invoiced" && <span className="text-emerald-700">✓ Đã xuất HĐ</span>}
                      {ctr.milestoneStatus === "paid" && <span className="text-emerald-700">✓ Đã thanh toán</span>}
                      {ctr.milestoneStatus === "pending" && <span className="text-amber-700">Đang chờ</span>}
                      {ctr.milestoneStatus === "delayed" && <span className="text-red-700">Trễ hạn</span>}
                    </span>
                  </div>
                </div>
              </div>
            </>
          ) : (
            <div className="text-xs text-[var(--color-muted-fg)]">
              Không thuộc hợp đồng dài hạn nào (bán lẻ / đơn hàng đơn lẻ).
            </div>
          )}
        </div>
      </div>
    </section>
  );
}

function ArContextPanel({ ar, currency, cashVoucher = false }: { ar: ArContextFound; currency: string; cashVoucher?: boolean }) {
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
            <TrendingUp className="mr-0.5 inline size-3" /> {cashVoucher ? "Sau chứng từ này" : "Sau hoá đơn này"}
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
          <div className="text-xs text-[var(--color-muted-fg)]">{cashVoucher ? "Hạn mức đã dùng" : "Dùng sau HĐ"}</div>
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

// UC3 §5.6 (Sprint 4): AP 3-way match panel — invoice_vat_in → PO → GRN.
// Clone của MatchContextPanel nhưng 2 card (không có purchase_contract Sprint 5).
function PoMatchPanel({ match, currency }: { match: PoMatchContextFound; currency: string }) {
  const po = match.purchase_order;
  const grn = match.goods_received_note;
  const isExact = match.quality === "exact";

  return (
    <section
      className={`space-y-3 rounded-[var(--radius)] border p-5 ${
        isExact ? "border-emerald-300 bg-emerald-50/40" : "border-amber-300 bg-amber-50/40"
      }`}
    >
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-serif text-lg">
            <ClipboardCheck className="mr-2 inline size-4" /> Đối chiếu 3 chiều AP
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            HĐ NCC ↔ Đơn đặt hàng (PO) ↔ Phiếu nhập kho (GRN)
          </p>
        </div>
        <span
          className={`inline-flex items-center gap-1 rounded-full px-3 py-1 text-xs font-medium ${
            isExact ? "bg-emerald-600 text-white" : "bg-amber-500 text-white"
          }`}
        >
          {isExact ? <CheckCircle2 className="size-3.5" /> : <AlertTriangle className="size-3.5" />}
          {isExact ? "Khớp đầy đủ" : "Khớp một phần"}
        </span>
      </div>

      <div className="grid gap-3 md:grid-cols-2">
        <div className="rounded-[var(--radius)] border bg-[var(--color-bg)] p-3 text-sm">
          <div className="mb-1 flex items-center gap-1 text-xs font-medium text-[var(--color-muted-fg)]">
            <FileText className="size-3.5" /> Đơn đặt hàng (PO)
          </div>
          <div className="font-mono font-bold">{po.poNumber}</div>
          <div className="mt-1 space-y-0.5 text-xs">
            <div>Ngày PO: {fmtDate(po.poDate)}</div>
            <div>NCC: <span className="font-mono">{po.supplierCode}</span></div>
            <div>Trạng thái: <span className="font-medium">{po.status}</span></div>
            <div>
              Subtotal: <span className="font-mono">{fmtMoney(po.subtotalVnd, "VND")}</span>
              {po.currency !== "VND" && po.exchangeRate > 0 && (
                <span className="ml-1 text-[10px] text-[var(--color-muted-fg)]">
                  (@ {po.exchangeRate.toLocaleString("vi-VN")} {po.currency}/VND)
                </span>
              )}
            </div>
            {po.totalAmount != null && (
              <div>
                Total (incl. thuế): <span className="font-mono">{fmtMoney(po.totalAmount, "VND")}</span>
              </div>
            )}
            {po.incoterm && <div>Incoterm: <span className="font-medium">{po.incoterm}</span></div>}
            {po.paymentTermsDays > 0 && (
              <div>Điều khoản: <span className="font-medium">Net {po.paymentTermsDays}</span></div>
            )}
            {po.matchedInvoiceNumber && (
              <div>
                HĐ khớp: <span className="font-mono text-emerald-700">{po.matchedInvoiceNumber}</span>
              </div>
            )}
          </div>
          {po.notes && (
            <div className="mt-2 border-t pt-2 text-[11px] italic text-[var(--color-muted-fg)]">
              {po.notes}
            </div>
          )}
        </div>

        <div
          className={`rounded-[var(--radius)] border p-3 text-sm ${
            grn ? "bg-[var(--color-bg)]" : "bg-amber-100/50"
          }`}
        >
          <div className="mb-1 flex items-center gap-1 text-xs font-medium text-[var(--color-muted-fg)]">
            <Package className="size-3.5" /> Phiếu nhập kho (GRN)
          </div>
          {grn ? (
            <>
              <div className="font-mono font-bold">{grn.grnNumber}</div>
              <div className="mt-1 space-y-0.5 text-xs">
                <div>Ngày nhận: {fmtDate(grn.grnDate)}</div>
                {grn.warehouseTo && <div>Kho nhận: <span className="font-medium">{grn.warehouseTo}</span></div>}
                <div>Trạng thái: <span className="font-medium">{grn.status}</span></div>
                {grn.deliveredBy && <div>Giao bởi: {grn.deliveredBy}</div>}
                {grn.receivedBy && <div>Nhận bởi: {grn.receivedBy}</div>}
              </div>
            </>
          ) : (
            <div className="text-xs text-amber-800">
              <b>Chưa có phiếu nhập kho.</b> Cần kiểm tra: hàng đã về kho chưa? Có nhận sai
              chứng từ khác?
            </div>
          )}
        </div>
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

// UC3 §5.9 mirror of ArContextPanel — AP payables snapshot cho NCC. Kế toán
// viên thấy công nợ 331 hiện tại, projected sau khi book HĐ mới, aging, và
// AP risk flags (TT78 cash > 30M, overdue, first transaction, v.v.)
function ApContextPanel({
  ap,
  currency,
  voucherId,
  voucherType,
  voucherStatus,
  invoiceTotal,
  invoiceSubtotal,
  invoiceVatAmount,
  isAdmin,
}: {
  ap: ApContextFound;
  currency: string;
  voucherId: string;
  voucherType: string;
  voucherStatus: string;
  invoiceTotal?: number | null;
  invoiceSubtotal?: number | null;
  invoiceVatAmount?: number | null;
  isAdmin: boolean;
}) {
  const invoiceDelta = ap.projected_balance_331 - ap.current_balance_331;
  const showBookButton = voucherType === "invoice_vat_in" && voucherStatus !== "verified";
  return (
    <section
      className={`space-y-4 rounded-[var(--radius)] border p-5 ${
        ap.requires_l2_approval ? "border-amber-300 bg-amber-50/40" : ""
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="font-serif text-lg">
            <Truck className="mr-2 inline size-4" /> Công nợ nhà cung cấp
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            <span className="font-mono">{ap.supplier.supplierCode}</span> · {ap.supplier.name}
            <span className="ml-2 rounded-full border px-1.5 py-0.5 text-[10px]">
              {ap.supplier.country} · {ap.supplier.currency}
            </span>
          </p>
        </div>
        {ap.requires_l2_approval && (
          <span className="inline-flex items-center gap-1 rounded-full bg-amber-600 px-3 py-1 text-xs font-medium text-white">
            <ShieldAlert className="size-3.5" /> Cần duyệt cấp 2
          </span>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Công nợ hiện tại (TK 331)</div>
          <div className="font-mono">{fmtMoney(ap.current_balance_331, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">
            <TrendingUp className="mr-0.5 inline size-3" /> Sau HĐ này
          </div>
          <div className={`font-mono font-bold ${invoiceDelta > 0 ? "text-amber-700" : ""}`}>
            {fmtMoney(ap.projected_balance_331, currency)}
            {invoiceDelta > 0 && (
              <span className="ml-1 text-xs font-normal text-[var(--color-muted-fg)]">
                (+{fmtMoney(invoiceDelta, currency)})
              </span>
            )}
          </div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Điều khoản TT mặc định</div>
          <div className="font-mono">
            {ap.payment_term_default > 0 ? `Net ${ap.payment_term_default}` : "Trả trước"}
          </div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Quá hạn</div>
          <div
            className={`font-mono font-bold ${
              ap.overdue_amount > 0 ? "text-red-700" : "text-emerald-700"
            }`}
          >
            {fmtMoney(ap.overdue_amount, currency)}
          </div>
        </div>
      </div>

      <div className="space-y-1">
        <div className="text-xs text-[var(--color-muted-fg)]">
          Tuổi nợ NCC (Aging) — tổng: {fmtMoney(ap.current_balance_331, currency)}
        </div>
        <div className="grid grid-cols-5 gap-2 text-xs">
          <AgingCell label="Trong hạn" amount={ap.aging.current} currency={currency} tone="emerald" />
          <AgingCell label="1-30 ngày" amount={ap.aging.bucket_0_30} currency={currency} tone="amber" />
          <AgingCell label="31-60 ngày" amount={ap.aging.bucket_31_60} currency={currency} tone="orange" />
          <AgingCell label="61-90 ngày" amount={ap.aging.bucket_61_90} currency={currency} tone="red" />
          <AgingCell label="> 90 ngày" amount={ap.aging.over90} currency={currency} tone="red-strong" />
        </div>
      </div>

      {ap.risk_flags.length > 0 && (
        <div className="space-y-1">
          <div className="text-xs text-[var(--color-muted-fg)]">Cảnh báo rủi ro (AP)</div>
          <ul className="space-y-1">
            {ap.risk_flags.map((flag) => (
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

      {ap.open_invoices.length > 0 && (
        <details className="text-xs">
          <summary className="cursor-pointer text-[var(--color-muted-fg)]">
            HĐ NCC chưa trả ({ap.open_invoices.length})
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
              {ap.open_invoices.slice(0, 10).map((inv) => (
                <tr key={inv.documentNumber} className="border-t">
                  <td className="px-2 py-1 font-mono">{inv.documentNumber}</td>
                  <td className="px-2 py-1 text-right font-mono">{fmtMoney(inv.credit, currency)}</td>
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

      {ap.last_payment_date && (
        <div className="text-[11px] text-[var(--color-muted-fg)]">
          Lần thanh toán gần nhất:{" "}
          <span className="font-medium">{fmtDate(ap.last_payment_date)}</span>
        </div>
      )}

      {/* Book invoice button — chỉ show cho invoice_vat_in, tăng AP. Status
          here is the voucher's own status — "extracted" reads as EXTRACTED,
          "verified" reads as POSTED (accounting effect created — never
          PAID; that is a separate, later payment flow). */}
      {showBookButton && (
        <div className="border-t pt-3">
          <div className="mb-1 text-sm font-medium">
            Trạng thái: {voucherStatus === "extracted" ? "EXTRACTED" : voucherStatus.toUpperCase()}
          </div>
          <div className="mb-2 text-xs text-[var(--color-muted-fg)]">
            Chiều hoá đơn: <b className="text-amber-800">MUA → Phải trả (AP)</b>
          </div>
          <p className="mb-1 text-xs text-[var(--color-muted-fg)]">
            Đối tác: <b>{ap.supplier.name}</b>
          </p>
          {invoiceTotal != null && (
            <div className="mb-2 space-y-0.5 rounded border bg-[var(--color-bg)] px-2 py-1.5 font-mono text-xs">
              <div>
                Phải trả {ap.supplier.name}: <b className="text-amber-800">+{fmtMoney(invoiceTotal, currency)}</b>
              </div>
              {invoiceSubtotal != null && <div>Hàng hoá/dịch vụ mua vào: {fmtMoney(invoiceSubtotal, currency)}</div>}
              {invoiceVatAmount != null && <div>VAT đầu vào: {fmtMoney(invoiceVatAmount, currency)}</div>}
            </div>
          )}
          {isAdmin ? (
            <AcceptExpenseButton voucherId={voucherId} voucherType={voucherType} />
          ) : (
            <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
              Chỉ ADMIN cùng phòng ban mới có thể Duyệt &amp; ghi nhận.
            </p>
          )}
          <p className="mt-2 text-[11px] italic text-[var(--color-muted-fg)]">
            Duyệt để ghi nhận HĐ NCC vào sổ AP (Có 331) + tăng công nợ phải trả. Không đồng
            nghĩa đã thanh toán. Idempotent.
          </p>
        </div>
      )}
      {voucherStatus === "verified" && voucherType === "invoice_vat_in" && (
        <div className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          <div className="font-medium">Trạng thái: POSTED</div>
          <div className="mt-1">
            ✓ Đã ghi nhận phải trả: {fmtMoney(invoiceTotal, currency)} — thanh toán:{" "}
            <b>Chưa trả (UNPAID)</b>. Xem trong{" "}
            <a href="/suppliers" className="underline">/suppliers</a>.
          </div>
        </div>
      )}
    </section>
  );
}

// Mirror ReceiptMatchingPanel cho phía AP — apply chi/uỷ nhiệm chi vào các
// HĐ NCC open (FIFO oldest first, không auto-detect discount).
function PaymentMatchingPanel({
  matching,
  currency,
  voucherId,
  voucherType,
  voucherStatus,
}: {
  matching: PaymentMatchingFound;
  currency: string;
  voucherId: string;
  voucherType: string;
  voucherStatus: string;
}) {
  const tone =
    matching.status === "overpaid"
      ? "border-amber-300 bg-amber-50/40"
      : matching.status === "no_open_payables"
        ? "border-blue-200 bg-blue-50/40"
        : "border-emerald-300 bg-emerald-50/40";
  return (
    <section className={`space-y-3 rounded-[var(--radius)] border p-5 ${tone}`}>
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-serif text-lg">
            <Banknote className="mr-2 inline size-4" /> Áp chi vào công nợ NCC
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">{matching.message}</p>
        </div>
        <div className="text-right">
          <div className="text-xs text-[var(--color-muted-fg)]">Số tiền chi</div>
          <div className="font-mono text-lg font-bold">
            {fmtMoney(matching.payment_amount, currency)}
          </div>
        </div>
      </div>

      {matching.applications.length > 0 && (
        <div className="overflow-hidden rounded-[var(--radius)] border bg-[var(--color-bg)]">
          <table className="w-full text-sm">
            <thead className="bg-[var(--color-muted)]/60 text-xs uppercase tracking-wider text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-2 text-left">Số HĐ NCC</th>
                <th className="px-3 py-2 text-left">Hạn TT</th>
                <th className="px-3 py-2 text-right">Còn phải trả trước</th>
                <th className="px-3 py-2 text-right">Áp chi</th>
                <th className="px-3 py-2 text-right">Còn phải trả sau</th>
              </tr>
            </thead>
            <tbody>
              {matching.applications.map((app) => (
                <tr key={app.document_number} className="border-t">
                  <td className="px-3 py-2 font-mono">{app.document_number}</td>
                  <td className="px-3 py-2 text-xs">{fmtDate(app.due_date)}</td>
                  <td className="px-3 py-2 text-right font-mono text-xs">
                    {fmtMoney(app.invoice_remaining_before, currency)}
                  </td>
                  <td className="px-3 py-2 text-right font-mono text-emerald-700">
                    {fmtMoney(app.applied, currency)}
                  </td>
                  <td
                    className={`px-3 py-2 text-right font-mono text-xs ${
                      app.invoice_remaining_after === 0 ? "text-emerald-700 font-bold" : ""
                    }`}
                  >
                    {fmtMoney(app.invoice_remaining_after, currency)}
                    {app.invoice_remaining_after === 0 && " ✓"}
                  </td>
                </tr>
              ))}
              <tr className="border-t bg-[var(--color-muted)]/30">
                <td colSpan={3} className="px-3 py-2 text-right text-xs font-medium">
                  Tổng áp chi:
                </td>
                <td className="px-3 py-2 text-right font-mono font-bold text-emerald-800">
                  {fmtMoney(matching.total_applied, currency)}
                </td>
                <td className="px-3 py-2" />
              </tr>
            </tbody>
          </table>
        </div>
      )}

      {matching.remaining_overpayment > 0 && (
        <div className="rounded-[var(--radius)] border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <b>Chi dư:</b> {fmtMoney(matching.remaining_overpayment, currency)} — treo TK 331 dư
          Nợ (ứng trước NCC) hoặc yêu cầu NCC hoàn lại.
        </div>
      )}

      {voucherStatus !== "verified" && (
        <p className="border-t pt-3 text-[11px] italic text-[var(--color-muted-fg)]">
          Bảng trên là gợi ý FIFO lúc trích xuất (tham khảo). Duyệt &amp; ghi nhận tại khung
          “Chi tiền · Đối trừ công nợ”.
        </p>
      )}
      {voucherStatus === "verified" && (
        <div className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          ✓ Đã áp chi vào công nợ NCC. Xem trong <a href="/suppliers" className="underline">/suppliers</a>.
        </div>
      )}
    </section>
  );
}

// Expense claim panel — hoàn ứng nhân viên (voucher_type=expense_claim). Show
// employee info, ytd_expense vs budget, journal breakdown TK 6417/141/111.
function ExpenseClaimPanel({
  claim,
  currency,
  voucherId,
  voucherType,
  voucherStatus,
}: {
  claim: ExpenseClaimMatchingFound;
  currency: string;
  voucherId: string;
  voucherType: string;
  voucherStatus: string;
}) {
  const emp = claim.employee;
  const budgetPct =
    claim.annual_ceiling > 0
      ? Math.round(((emp.ytdExpense + claim.total_claim) / claim.annual_ceiling) * 100)
      : 0;
  return (
    <section
      className={`space-y-4 rounded-[var(--radius)] border p-5 ${
        claim.budget_exceeded ? "border-red-300 bg-red-50/40" : "border-emerald-300 bg-emerald-50/40"
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="font-serif text-lg">
            <UserCircle2 className="mr-2 inline size-4" /> Hoàn ứng nhân viên
          </h2>
          <p className="text-xs text-[var(--color-muted-fg)]">
            <span className="font-mono">{emp.employeeCode}</span> · {emp.fullName}
            {emp.department && (
              <span className="ml-2 rounded-full border px-1.5 py-0.5 text-[10px]">
                {emp.department}
              </span>
            )}
          </p>
        </div>
        {claim.budget_exceeded && (
          <span className="inline-flex items-center gap-1 rounded-full bg-red-600 px-3 py-1 text-xs font-medium text-white">
            <AlertTriangle className="size-3.5" /> Vượt ngân sách năm
          </span>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Tổng chi claim</div>
          <div className="font-mono font-bold">{fmtMoney(claim.total_claim, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Tạm ứng đã lấy (TK 141)</div>
          <div className="font-mono">{fmtMoney(emp.outstandingAdvance, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Đối trừ ứng</div>
          <div className="font-mono text-blue-700">{fmtMoney(claim.advance_covered, currency)}</div>
        </div>
        <div>
          <div className="text-xs text-[var(--color-muted-fg)]">Hoàn tiền cho NV</div>
          <div className="font-mono font-bold text-emerald-700">
            {fmtMoney(claim.remaining_to_reimburse, currency)}
          </div>
        </div>
      </div>

      {claim.annual_ceiling > 0 && (
        <div className="space-y-1">
          <div className="flex items-center justify-between text-xs">
            <span className="text-[var(--color-muted-fg)]">
              Chi năm nay (YTD): {fmtMoney(emp.ytdExpense, currency)} / {fmtMoney(claim.annual_ceiling, currency)}
            </span>
            <span
              className={`font-mono font-bold ${
                budgetPct >= 100 ? "text-red-700" : budgetPct >= 80 ? "text-amber-700" : "text-emerald-700"
              }`}
            >
              {budgetPct}%
            </span>
          </div>
          <div className="h-2 overflow-hidden rounded-full bg-[var(--color-muted)]">
            <div
              className={`h-full ${
                budgetPct >= 100 ? "bg-red-600" : budgetPct >= 80 ? "bg-amber-500" : "bg-emerald-500"
              }`}
              style={{ width: `${Math.min(100, budgetPct)}%` }}
            />
          </div>
        </div>
      )}

      {claim.items && claim.items.length > 0 && (
        <div className="overflow-hidden rounded-[var(--radius)] border bg-[var(--color-bg)]">
          <div className="border-b bg-[var(--color-muted)]/40 px-3 py-1.5 text-xs font-medium text-[var(--color-muted-fg)]">
            Chi tiết {claim.items.length} khoản chi
          </div>
          <table className="w-full text-sm">
            <thead className="text-xs text-[var(--color-muted-fg)]">
              <tr>
                <th className="px-3 py-1.5 text-left">Loại</th>
                <th className="px-3 py-1.5 text-left">Nội dung</th>
                <th className="px-3 py-1.5 text-center">TK Nợ</th>
                <th className="px-3 py-1.5 text-right">Số tiền</th>
                <th className="px-3 py-1.5 text-center">HĐ</th>
              </tr>
            </thead>
            <tbody>
              {claim.items.map((it, idx) => (
                <tr key={idx} className="border-t">
                  <td className="px-3 py-1.5 text-xs">{it.category}</td>
                  <td className="px-3 py-1.5 text-xs">{it.description}</td>
                  <td className="px-3 py-1.5 text-center">
                    <span
                      className={`inline-block rounded-full border px-1.5 py-0.5 font-mono text-[10px] ${
                        it.tt96_flagged
                          ? "border-amber-300 bg-amber-50 text-amber-800"
                          : "border-gray-200 bg-gray-50 text-gray-700"
                      }`}
                    >
                      {it.journal_account}
                    </span>
                  </td>
                  <td className="px-3 py-1.5 text-right font-mono text-xs">
                    {fmtMoney(it.amount, currency)}
                  </td>
                  <td className="px-3 py-1.5 text-center text-[10px] text-[var(--color-muted-fg)]">
                    {it.receipt_attached && it.receipt_attached.length > 0 ? it.receipt_attached : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {claim.has_tt96_category && (
        <div className="rounded-[var(--radius)] border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          <AlertTriangle className="mr-1 inline size-3.5" />
          <b>Cảnh báo TT96:</b> Có khoản chi quà/tiếp khách hạch toán TK 6428 — chi phí tiếp
          khách được khấu trừ tối đa 15% tổng chi phí (Thông tư 96/2015). Cần theo dõi riêng.
        </div>
      )}

      <div className="rounded-[var(--radius)] border bg-[var(--color-bg)] px-3 py-2">
        <div className="mb-1 text-xs text-[var(--color-muted-fg)]">Bút toán tổng hợp</div>
        <div className="space-y-0.5 font-mono text-xs">
          {claim.journal_breakdown.map((line, idx) => (
            <div key={idx}>
              Nợ <span className={line.tt96_flagged ? "text-amber-700 font-bold" : "font-bold"}>
                {line.debit_account}
              </span>{" "}
              ({line.category}) &nbsp;{fmtMoney(line.amount, currency)}
            </div>
          ))}
          {claim.advance_covered > 0 && (
            <div className="text-blue-700">
              Có <b>141</b> (tạm ứng) &nbsp;{fmtMoney(claim.advance_covered, currency)}
            </div>
          )}
          {claim.remaining_to_reimburse > 0 && (
            <div className="text-emerald-700">
              Có <b>111</b> (tiền mặt hoàn NV) &nbsp;{fmtMoney(claim.remaining_to_reimburse, currency)}
            </div>
          )}
        </div>
      </div>

      {voucherStatus !== "verified" && (
        <div className="border-t pt-3">
          <AcceptExpenseButton voucherId={voucherId} voucherType={voucherType} />
          <p className="mt-2 text-[11px] italic text-[var(--color-muted-fg)]">
            Bấm để giảm TK 141 (tạm ứng) + tăng ytd_expense của nhân viên. Idempotent.
          </p>
        </div>
      )}
      {voucherStatus === "verified" && (
        <div className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 px-3 py-2 text-sm text-emerald-900">
          ✓ Đã hoàn ứng. sim_employees.outstanding_advance đã cập nhật.
        </div>
      )}
    </section>
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
  const reviewRes = await gateway.listVoucherDocumentReviews(token, id);
  const stateRes = await gateway.getVoucherProcessingState(token, id);
  const processingState = stateRes.ok
    ? ((await stateRes.json()) as { extractionError: { code: string; stage: string | null; failedAt: string | null } | null; blockingReasons: string[] })
    : { extractionError: null, blockingReasons: [] as string[] };
  const documentReview = reviewRes.ok
    ? ((await reviewRes.json()) as { reviewStatus: string | null; items: DocumentReviewTask[] })
    : { reviewStatus: null, items: [] as DocumentReviewTask[] };

  // Luồng 02 — ADMIN-only confirm/reject actions must not render for a plain
  // USER (backend still enforces this via department_admin, but the task
  // explicitly asks the UI not to show ADMIN-only actions to USER here).
  let isAdmin = false;
  const meRes = await gateway.me(token);
  if (meRes.ok) {
    const me = (await meRes.json()) as { roles?: string[] };
    isAdmin = (me.roles ?? []).includes("admin");
  }
  const isCashVoucher = v.voucherType === "receipt" || v.voucherType === "payment" || v.voucherType === "bank_transfer";
  let settlement: { context: SettlementContext; canApprove: boolean } | null = null;
  let settlementError: string | null = null;
  if (isCashVoucher) {
    const settlementRes = await gateway.getSettlement(token, id);
    if (settlementRes.ok) {
      settlement = (await settlementRes.json()) as { context: SettlementContext; canApprove: boolean };
    } else {
      settlementError = `Không tải được thông tin thu/chi (HTTP ${settlementRes.status}).`;
    }
  }
  const receiptMatchRes = v.voucherType === "receipt" ? await gateway.getReceiptMatch(token, id) : null;
  const receiptMatch = receiptMatchRes?.ok ? ((await receiptMatchRes.json()) as ReceiptMatchResult) : null;
  const expenseClaimResultRes = v.voucherType === "expense_claim" ? await gateway.getExpenseClaimResult(token, id) : null;
  const expenseClaimResult = expenseClaimResultRes?.ok ? ((await expenseClaimResultRes.json()) as ExpenseClaimResult) : null;
  const currency = v.currency ?? "VND";

  // UC-ACC-001 §5.5-5.7: parse A/R context enriched at upload time.
  // UC3: parse AP-side context (ap_context, payment_matching, expense_claim_matching).
  let arContext: ArContext | null = null;
  let matchContext: MatchContext | null = null;
  let receiptMatching: ReceiptMatching | null = null;
  let apContext: ApContext | null = null;
  let paymentMatching: PaymentMatching | null = null;
  let expenseClaimMatching: ExpenseClaimMatching | null = null;
  let poMatchContext: PoMatchContext | null = null;
  if (v.extractedFieldsJson) {
    try {
      const parsed = JSON.parse(v.extractedFieldsJson) as {
        ar_context?: ArContext;
        match_context?: MatchContext;
        receipt_matching?: ReceiptMatching;
        ap_context?: ApContext;
        payment_matching?: PaymentMatching;
        expense_claim_matching?: ExpenseClaimMatching;
        po_match_context?: PoMatchContext;
      };
      arContext = parsed.ar_context ?? null;
      matchContext = parsed.match_context ?? null;
      receiptMatching = parsed.receipt_matching ?? null;
      apContext = parsed.ap_context ?? null;
      paymentMatching = parsed.payment_matching ?? null;
      expenseClaimMatching = parsed.expense_claim_matching ?? null;
      poMatchContext = parsed.po_match_context ?? null;
    } catch {
      arContext = null;
      matchContext = null;
      receiptMatching = null;
      apContext = null;
      paymentMatching = null;
      expenseClaimMatching = null;
      poMatchContext = null;
    }
  }

  // extracted_fields.ar_context / ap_context are snapshots from extraction
  // time; after any posting/unapply/reverse they are stale. Re-read the live
  // receivables/payables report so every panel shows the same balance.
  if (arContext && arContext.status === "found") {
    const customerId = snapshotPartyId(arContext.customer as unknown as Record<string, unknown>);
    const liveRes = customerId ? await gateway.customerReceivables(token, customerId) : null;
    if (liveRes?.ok) {
      const snap = arContext;
      arContext = overlayLiveAr(snap, (await liveRes.json()) as LiveReceivables,
        pendingBalanceDelta(v.voucherType, v.status, snap.current_balance_131, snap.projected_balance_131)) as ArContextFound;
    }
  }
  if (apContext && apContext.status === "found") {
    const supplierId = snapshotPartyId(apContext.supplier as unknown as Record<string, unknown>);
    const liveRes = supplierId ? await gateway.supplierPayables(token, supplierId) : null;
    if (liveRes?.ok) {
      const snap = apContext;
      apContext = overlayLiveAp(snap, (await liveRes.json()) as LivePayables,
        pendingBalanceDelta(v.voucherType, v.status, snap.current_balance_331, snap.projected_balance_331)) as ApContextFound;
    }
  }
  const partyCards = buildPartyCards(v, settlement?.context.parties ?? null);

  // Bank statement: individual transaction lines + Customer identification.
  let statement: { transactions: StatementTransactionView[]; canReview: boolean } | null = null;
  let customerOptions: PartyOption[] = [];
  let supplierOptions: PartyOption[] = [];
  let companyAccounts: CompanyAccountOption[] = [];
  if (v.voucherType === "bank_statement") {
    const stRes = await gateway.voucherBankTransactions(token, id);
    if (stRes.ok) statement = (await stRes.json()) as { transactions: StatementTransactionView[]; canReview: boolean };
    if (statement?.canReview) {
      const [cRes, sRes] = await Promise.all([gateway.listCustomers(token), gateway.listSuppliers(token)]);
      type Item = { id: string; customerCode?: string; supplierCode?: string; name: string };
      if (cRes.ok) customerOptions = ((await cRes.json()) as { items: Item[] }).items.map((c) => ({ id: c.id, code: c.customerCode ?? "", name: c.name }));
      if (sRes.ok) supplierOptions = ((await sRes.json()) as { items: Item[] }).items.map((c) => ({ id: c.id, code: c.supplierCode ?? "", name: c.name }));
      const caRes = await gateway.listCompanyBankAccounts(token);
      if (caRes.ok) companyAccounts = (await caRes.json()) as CompanyAccountOption[];
    }
  }
  const settlementPosted = settlement?.context.posting != null;

  // Auto-refresh while the async OCR + extraction pipeline is still running.
  // Once we reach 'extracted' the fields + journal are present and refresh
  // stops. Users who navigated here right after upload see the page update
  // itself without manual F5.
  const isPipelineRunning =
    v.status === "uploading" || v.status === "processing" || v.status === "ocr_complete";

  return (
    <div className="space-y-6">
      {isPipelineRunning && (
        <meta httpEquiv="refresh" content="5" />
      )}
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
            {voucherTypeLabel(v.voucherType)} ·{" "}
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

      {/* Voucher type: AIQ detection (type, confidence, evidence) and the
          ADMIN confirm/correct controls. Shown once extraction has run. */}
      {!isPipelineRunning && (parseClassification(v.extractedFieldsJson) || v.voucherType === "unclassified") && (
        <VoucherTypeReview
          voucherId={v.id}
          currentType={v.voucherType}
          classification={parseClassification(v.extractedFieldsJson)}
          canDecide={isAdmin}
          posted={v.status === "verified" || settlementPosted}
        />
      )}

      {/* Multi-file claim: one item per bill file (renders nothing for a legacy claim). */}
      {v.voucherType === "expense_claim" && <ExpenseClaimItemsPanel claimId={v.id} />}

      {processingState.extractionError && (
        <ExtractionFailureBanner
          voucherId={v.id}
          voucherType={v.voucherType}
          code={processingState.extractionError.code}
          failedAt={processingState.extractionError.failedAt}
          keptPrevious={v.status !== "extraction_failed"}
        />
      )}

      <DocumentReviewPanel
        voucherId={v.id}
        reviewStatus={documentReview.reviewStatus}
        tasks={documentReview.items}
        canDecide={isAdmin}
      />

      {(() => {
        const dp = documentProcessingFrom(v.extractedFieldsJson);
        if (!dp) return null;
        return (
          <p className="text-xs text-[var(--color-muted-fg)]" data-testid="document-provenance">
            Xử lý bởi {dp.agent_name ?? "trợ lý"}
            {dp.agent_version != null ? ` v${dp.agent_version}` : ""} · cấu hình:{" "}
            <span className="font-mono">{dp.config_source}</span>
            {dp.model ? ` · ${dp.provider}/${dp.model}` : ""}
            {dp.schema_valid === false ? " · schema chưa hợp lệ" : ""}
            {dp.unsupported_document_type ? " · loại chứng từ không được trợ lý hỗ trợ" : ""}
          </p>
        );
      })()}

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
          voucherStatus={v.status}
        />
      )}

      {/* Party labels follow the voucher type: Bên nộp/nhận tiền for a
          phiếu thu, Bên trả/nhận tiền for phiếu chi/UNC, Bên bán/mua only
          for invoices. */}
      {v.voucherType !== "bank_statement" && <div className="grid gap-4 lg:grid-cols-2">
        {partyCards.map((card) => (
          <section key={card.title} className="space-y-3 rounded-[var(--radius)] border p-5">
            <h2 className="font-serif text-lg">{card.title}</h2>
            <dl className="space-y-1 text-sm">
              <div>
                <dt className="text-xs text-[var(--color-muted-fg)]">Tên</dt>
                <dd>{card.name ?? "—"}</dd>
              </div>
              <div>
                <dt className="text-xs text-[var(--color-muted-fg)]">MST</dt>
                <dd className="font-mono">{card.taxCode ?? "—"}</dd>
              </div>
              <div>
                <dt className="text-xs text-[var(--color-muted-fg)]">Địa chỉ</dt>
                <dd>{card.address ?? "—"}</dd>
              </div>
            </dl>
          </section>
        ))}
      </div>}

      {statement && (
        <BankStatementPanel
          voucherId={v.id}
          transactions={statement.transactions}
          canReview={statement.canReview}
          customers={customerOptions}
          suppliers={supplierOptions}
          importNote={importNoteFrom(v.extractedFieldsJson)}
          companyAccounts={companyAccounts}
        />
      )}

      {/* Chứng từ THU / CHI → ADMIN duyệt → tài khoản tiền → đối trừ AR/AP. */}
      {settlement && (
        <CashSettlementPanel voucherId={v.id} context={settlement.context} canApprove={settlement.canApprove} />
      )}
      {settlementError && (
        <section className="rounded-[var(--radius)] border border-red-200 bg-red-50 p-4 text-sm text-red-700">
          {settlementError}
        </section>
      )}

      {/* UC-ACC-001 §5.9 + BR-5: A/R context panel — full receivables snapshot
          of the buyer so the accountant sees credit-limit impact + risk flags
          before approving the AI-suggested journal. */}
      {arContext && arContext.status === "found" && (
        <>
          <ArContextPanel ar={arContext} currency={currency} cashVoucher={isCashVoucher} />
          {/* Sprint 4 gap UC1 / invoice approval lifecycle: book invoice
              into AR ledger. Status here is the voucher's own status —
              "extracted" reads as EXTRACTED (data extracted only, not yet
              posted), "verified" reads as POSTED (accounting effect
              created — never PAID; that is a separate, later payment flow). */}
          {(v.voucherType === "invoice_vat_out" || v.voucherType === "invoice_vat") &&
            v.status !== "verified" && (
              <section className="rounded-[var(--radius)] border border-emerald-200 bg-emerald-50/30 p-4">
                <div className="mb-1 text-sm font-medium">
                  Trạng thái: {v.status === "extracted" ? "EXTRACTED" : v.status.toUpperCase()}
                </div>
                <div className="mb-2 text-xs text-[var(--color-muted-fg)]">
                  Chiều hoá đơn: <b className="text-emerald-800">BÁN → Phải thu (AR)</b>
                </div>
                <p className="mb-1 text-xs text-[var(--color-muted-fg)]">
                  Đối tác: <b>{v.buyerName ?? "—"}</b>
                </p>
                <div className="mb-2 space-y-0.5 rounded border bg-[var(--color-bg)] px-2 py-1.5 font-mono text-xs">
                  <div>
                    Phải thu {v.buyerName ?? "khách hàng"}:{" "}
                    <b className="text-emerald-800">+{fmtMoney(v.totalAmount, currency)}</b>
                  </div>
                  {v.subtotal != null && <div>Doanh thu: {fmtMoney(v.subtotal, currency)}</div>}
                  {v.vatAmount != null && <div>VAT đầu ra: {fmtMoney(v.vatAmount, currency)}</div>}
                </div>
                {isAdmin ? (
                  <AcceptInvoiceButton voucherId={v.id} />
                ) : (
                  <p className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
                    Chỉ ADMIN cùng phòng ban mới có thể Duyệt &amp; ghi nhận.
                  </p>
                )}
                <p className="mt-2 text-[11px] italic text-[var(--color-muted-fg)]">
                  Duyệt để ghi nhận HĐ vào sổ AR (Nợ 131 / Có 511 / Có 33311) + tăng công nợ
                  phải thu của khách. Không đồng nghĩa đã thu tiền. Idempotent.
                </p>
              </section>
            )}
          {(v.voucherType === "invoice_vat_out" || v.voucherType === "invoice_vat") &&
            v.status === "verified" && (
              <section className="rounded-[var(--radius)] border border-emerald-300 bg-emerald-100 p-3 text-sm text-emerald-900">
                <div className="font-medium">Trạng thái: POSTED</div>
                <div className="mt-1">
                  ✓ Đã ghi nhận phải thu: {fmtMoney(v.totalAmount, currency)} — thanh toán:{" "}
                  <b>Chưa thu (UNPAID)</b>. Xem trong{" "}
                  <a href="/customers" className="underline">/customers</a>.
                </div>
              </section>
            )}
        </>
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

      {receiptMatch && (
        <section className="rounded-[var(--radius)] border p-4">
          <div className="mb-2 flex items-center justify-between">
            <h2 className="font-serif text-lg">Luồng 02 — Kết quả khớp công nợ</h2>
            <span
              className={`rounded-full border px-2 py-0.5 text-xs ${
                settlementPosted || receiptMatch.reviewStatus === "confirmed"
                  ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                  : receiptMatch.reviewStatus === "rejected"
                    ? "border-gray-300 bg-gray-50 text-gray-600"
                    : "border-amber-300 bg-amber-50 text-amber-700"
              }`}
            >
              {settlementPosted
                ? "Đã ghi nhận qua Thu/Chi"
                : receiptMatch.reviewStatus === "confirmed"
                ? "Đã xác nhận"
                : receiptMatch.reviewStatus === "rejected"
                  ? "Đã từ chối"
                  : "Đang chờ duyệt"}
            </span>
          </div>
          <div className="mb-3 inline-block rounded-full border px-2 py-1 text-xs font-mono">
            {receiptMatch.outcome}
          </div>
          <div className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
            <div>Khách hàng khớp: <b className="font-mono">{receiptMatch.customerMatchConfidence * 100}%</b></div>
            <div>Căn cứ: {receiptMatch.customerMatchEvidence ?? "—"}</div>
            <div>Công nợ/hoá đơn: {receiptMatch.receivableReference ?? "—"}</div>
            <div>Hợp đồng/đợt thanh toán: {receiptMatch.installmentId ? "Có liên kết" : "—"}</div>
            <div>Số tiền phiếu thu: <b className="font-mono">{receiptMatch.receiptAmount.toLocaleString("vi-VN")} {receiptMatch.currency}</b></div>
            <div>Đã khớp: <b className="font-mono">{receiptMatch.matchedAmount.toLocaleString("vi-VN")}</b></div>
            <div>Chưa phân bổ: <b className="font-mono">{receiptMatch.unallocatedAmount.toLocaleString("vi-VN")}</b></div>
          </div>
          {settlementPosted && (
            <p className="mt-2 text-xs text-[var(--color-muted-fg)]">
              Phân loại lúc trích xuất (tham khảo). Bút toán và phân bổ thực tế đã ghi nhận ở khung
              “Thu tiền / Chi tiền · Đối trừ công nợ”.
            </p>
          )}
          {isAdmin && !settlementPosted && receiptMatch.reviewStatus === "proposed" && (
            <div className="mt-3">
              <RejectReceiptMatchButton voucherId={v.id} />
            </div>
          )}
        </section>
      )}

      {expenseClaimResult && (
        <section className="rounded-[var(--radius)] border p-4">
          <div className="mb-2 flex items-center justify-between">
            <h2 className="font-serif text-lg">Luồng 03 — Kết quả xử lý đề nghị hoàn ứng</h2>
            <span
              className={`rounded-full border px-2 py-0.5 text-xs ${
                expenseClaimResult.reviewStatus === "confirmed"
                  ? "border-emerald-300 bg-emerald-50 text-emerald-700"
                  : expenseClaimResult.reviewStatus === "rejected"
                    ? "border-gray-300 bg-gray-50 text-gray-600"
                    : "border-amber-300 bg-amber-50 text-amber-700"
              }`}
            >
              {expenseClaimResult.reviewStatus === "confirmed"
                ? "Đã xác nhận"
                : expenseClaimResult.reviewStatus === "rejected"
                  ? "Đã từ chối"
                  : expenseClaimResult.reviewStatus === "dept_reviewed"
                    ? "Đã duyệt bộ phận — chờ kế toán"
                    : expenseClaimResult.reviewStatus === "accounting_reviewed"
                      ? "Đã duyệt kế toán — chờ duyệt chi"
                      : "Chờ duyệt bộ phận"}
            </span>
          </div>
          <div className="mb-3 inline-block rounded-full border px-2 py-1 text-xs font-mono">
            {expenseClaimResult.outcome}
          </div>
          <div className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
            <div>Số tiền đề nghị: <b className="font-mono">{expenseClaimResult.requestedAmount.toLocaleString("vi-VN")} {expenseClaimResult.currency}</b></div>
            <div>Số tiền được duyệt: <b className="font-mono">{expenseClaimResult.approvedAmount.toLocaleString("vi-VN")}</b></div>
            <div>Bị chặn (trùng): <b className="font-mono">{expenseClaimResult.blockedAmount.toLocaleString("vi-VN")}</b></div>
            <div>Bị cắt (vượt định mức): <b className="font-mono">{expenseClaimResult.cappedAmount.toLocaleString("vi-VN")}</b></div>
            <div>Chi phí không được trừ (TNDN): <b className="font-mono">{expenseClaimResult.nonDeductibleAmount.toLocaleString("vi-VN")}</b></div>
            <div>Thuế GTGT được khấu trừ: <b className="font-mono">{expenseClaimResult.vatDeductibleAmount.toLocaleString("vi-VN")}</b> / {expenseClaimResult.vatAmount.toLocaleString("vi-VN")}</div>
            <div>Tạm ứng đối trừ: <b className="font-mono">{expenseClaimResult.advanceAmount.toLocaleString("vi-VN")}</b></div>
            <div>
              {expenseClaimResult.payableAmount >= 0 ? "Còn phải trả nhân viên" : "Nhân viên phải hoàn lại"}:{" "}
              <b className="font-mono">{Math.abs(expenseClaimResult.payableAmount).toLocaleString("vi-VN")}</b>
            </div>
          </div>
          {expenseClaimResult.missingDocuments && (
            <p className="mt-2 text-xs text-amber-700">{expenseClaimResult.missingDocuments}</p>
          )}
          {(() => {
            // U2 audit fix — per-line Đạt/Cắt/Chặn breakdown. Parsed straight
            // from the same duplicate/policy flag data the backend already
            // computes and stores; nothing here is fabricated — a line with
            // no flag at all is simply not listed (it was "Đạt").
            let duplicateFlags: Array<{ seq: number; reason: string; vendorMst?: string; invoiceNo?: string }> = [];
            let policyFlags: Array<{ seq: number | null; category: string; rule: string; limitAmount: number; limitUnit: string; claimedAmount: number }> = [];
            try { duplicateFlags = JSON.parse(expenseClaimResult.duplicateFlagsJson || "[]"); } catch { /* leave empty */ }
            try { policyFlags = JSON.parse(expenseClaimResult.policyFlagsJson || "[]"); } catch { /* leave empty */ }
            if (duplicateFlags.length === 0 && policyFlags.length === 0) return null;
            return (
              <div className="mt-3 space-y-1 border-t pt-2 text-xs">
                {policyFlags.map((f, i) => (
                  <div key={`policy-${i}`} className="text-amber-700">
                    {f.seq != null ? `Dòng ${f.seq}` : "Toàn bộ đề nghị"} ({f.category}): Cắt về {f.limitAmount.toLocaleString("vi-VN")} {f.limitUnit === "per_item" ? "/dòng" : ""} — đề nghị {f.claimedAmount.toLocaleString("vi-VN")}
                  </div>
                ))}
                {duplicateFlags.map((f, i) => (
                  <div key={`dup-${i}`} className="text-red-700">
                    Dòng {f.seq}: Chặn — {f.reason === "duplicate_within_claim" ? "trùng trong cùng đề nghị" : "đã ghi sổ/claim trước đó"}
                    {f.invoiceNo ? ` (${f.invoiceNo})` : ""}
                  </div>
                ))}
              </div>
            );
          })()}
          {isAdmin && expenseClaimResult.reviewStatus === "proposed" && (
            <div className="mt-3 flex items-center gap-2">
              {expenseClaimResult.outcome === "WAITING_EXCEPTION_APPROVAL" && (
                <GrantExpenseExceptionButton voucherId={v.id} />
              )}
              {(expenseClaimResult.outcome === "APPROVED" || expenseClaimResult.outcome === "PARTIALLY_APPROVED") && (
                <DepartmentReviewExpenseButton voucherId={v.id} />
              )}
              {expenseClaimResult.outcome !== "RETURN_FOR_DOCUMENTS" && expenseClaimResult.outcome !== "REJECTED" && (
                <RejectExpenseClaimButton voucherId={v.id} />
              )}
            </div>
          )}
          {isAdmin && expenseClaimResult.reviewStatus === "dept_reviewed" && (
            <div className="mt-3 flex items-center gap-2">
              <AccountingReviewExpenseButton voucherId={v.id} />
              <RejectExpenseClaimButton voucherId={v.id} />
            </div>
          )}
          {isAdmin && expenseClaimResult.reviewStatus === "accounting_reviewed" && (
            <div className="mt-3 flex items-center gap-2">
              {/* Cash due to the employee -> "Duyệt & chi tiền" below; the
                  old no-cash final approval only when nothing is to be paid. */}
              {expenseClaimResult.payableAmount <= 0 && <AcceptExpenseButton voucherId={v.id} voucherType={v.voucherType} />}
              <RejectExpenseClaimButton voucherId={v.id} />
            </div>
          )}
          {v.voucherType === "expense_claim" && (
            <div className="mt-3">
              <ExpenseClaimPaymentPanel voucherId={v.id} />
            </div>
          )}
        </section>
      )}

      {receiptMatching && !settlement && (
        <>
          <ReceiptMatchingPanel
            matching={receiptMatching}
            currency={currency}
            voucherId={v.id}
            voucherStatus={v.status}
          />
        </>
      )}

      {/* UC3 AP-side panels — mirror của AR panels ở trên. */}
      {apContext && apContext.status === "found" && (
        <ApContextPanel
          ap={apContext}
          currency={currency}
          voucherId={v.id}
          voucherType={v.voucherType}
          voucherStatus={v.status}
          invoiceTotal={v.totalAmount}
          invoiceSubtotal={v.subtotal}
          invoiceVatAmount={v.vatAmount}
          isAdmin={isAdmin}
        />
      )}
      {apContext && apContext.status !== "found" && (
        <section className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
          <div className="flex items-start gap-2">
            <Truck className="mt-0.5 size-4 shrink-0" />
            <div>
              <p className="font-medium">Nhà cung cấp chưa có trong hệ thống</p>
              <p className="text-xs">
                MST <span className="font-mono">{apContext.supplier_mst ?? "?"}</span> không khớp với
                bất kỳ NCC nào. Tạo hồ sơ NCC mới hoặc kiểm tra lại MST trên hoá đơn.
              </p>
            </div>
          </div>
        </section>
      )}

      {paymentMatching && !settlement && (
        <PaymentMatchingPanel
          matching={paymentMatching}
          currency={currency}
          voucherId={v.id}
          voucherType={v.voucherType}
          voucherStatus={v.status}
        />
      )}

      {["invoice_vat_in", "invoice_vat_out", "invoice_vat", "expense_claim"].includes(v.voucherType) && (
        <TaxVerificationPanel voucherId={v.id} />
      )}

      {expenseClaimMatching && expenseClaimMatching.status === "found" && (
        <ExpenseClaimPanel
          claim={expenseClaimMatching}
          currency={currency}
          voucherId={v.id}
          voucherType={v.voucherType}
          voucherStatus={v.status}
        />
      )}
      {expenseClaimMatching && expenseClaimMatching.status === "employee_not_found" && (
        <section className="rounded-[var(--radius)] border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
          <div className="flex items-start gap-2">
            <UserCircle2 className="mt-0.5 size-4 shrink-0" />
            <div>
              <p className="font-medium">Nhân viên chưa có trong hệ thống</p>
              <p className="text-xs">
                Mã NV <span className="font-mono">{expenseClaimMatching.employee_code ?? "?"}</span>{" "}
                không khớp. Cần tạo hồ sơ nhân viên trước khi hoàn ứng.
              </p>
            </div>
          </div>
        </section>
      )}

      {matchContext && matchContext.status === "found" && (
        <MatchContextPanel match={matchContext} currency={currency} />
      )}

      {/* Sprint 4: AP 3-way match (invoice_vat_in → PO → GRN). */}
      {poMatchContext && poMatchContext.status === "found" && (
        <PoMatchPanel match={poMatchContext} currency={currency} />
      )}
      {poMatchContext && poMatchContext.status === "no_match" && (
        <section className="rounded-[var(--radius)] border border-blue-200 bg-blue-50 p-4 text-sm text-blue-800">
          <div className="flex items-start gap-2">
            <FileText className="mt-0.5 size-4 shrink-0" />
            <div>
              <p className="font-medium">Không tìm thấy PO khớp</p>
              <p className="text-xs">
                HĐ NCC <span className="font-mono">{poMatchContext.document_number ?? "?"}</span>{" "}
                chưa link tới đơn đặt hàng nào. Có thể là mua ngoài PO (nếu &lt; 100M) hoặc extract
                sai số HĐ. Kiểm tra trước khi accept.
              </p>
            </div>
          </div>
        </section>
      )}
      {matchContext && matchContext.status === "no_match" && (
        <section className="rounded-[var(--radius)] border border-blue-200 bg-blue-50 p-4 text-sm text-blue-800">
          <div className="flex items-start gap-2">
            <ClipboardCheck className="mt-0.5 size-4 shrink-0" />
            <div>
              <p className="font-medium">Bán hàng không qua đơn hàng (không tìm thấy PO)</p>
              <p className="text-xs">
                Hoá đơn số <span className="font-mono">{matchContext.document_number ?? "?"}</span>{" "}
                không khớp với sales order nào — có thể là bán retail hoặc order verbal chưa
                nhập hệ thống. Bắt buộc duyệt thủ công (UC-ACC-001 A2).
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
        <h2 className="font-serif text-lg">Bút toán (TT200)</h2>
        {splitJournal(detail.journalSuggestions).posted.length > 0 && (
          <div className="space-y-1" data-testid="posted-journal">
            <div className="text-sm font-medium text-emerald-800">Bút toán đã ghi nhận (ghi sổ)</div>
            <table className="w-full text-sm">
              <tbody>
                {splitJournal(detail.journalSuggestions).posted.map((e) => (
                  <tr key={e.id} className="border-t">
                    <td className="px-3 py-1 font-mono">{e.entryNo}</td>
                    <td className="px-3 py-1 font-mono">Nợ {e.debitAccount}</td>
                    <td className="px-3 py-1 font-mono">Có {e.creditAccount}</td>
                    <td className="px-3 py-1 text-right font-mono">{fmtMoney(e.amount, currency)}</td>
                    <td className="px-3 py-1 text-xs">{e.description ?? "—"}{e.source === "reversal" ? " (đảo)" : ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="text-sm font-medium" data-testid="ai-suggestions-title">Gợi ý AI (chỉ tham khảo — không phải bút toán đã ghi sổ)</div>
        {splitJournal(detail.journalSuggestions).suggestions.length === 0 ? (
          (() => {
            // BR09 audit fix — the generic "AI will suggest soon" message is
            // actively misleading for outcomes that will NEVER auto-populate
            // a journal entry (no rate source, no payment method, or an
            // outcome the business process document says needs a human
            // decision, never an invented entry). Say so plainly instead of
            // implying a transient wait.
            if (receiptMatch?.outcome === "FOREIGN_CURRENCY") {
              return (
                <p className="text-sm text-amber-700">
                  Giao dịch ngoại tệ — hệ thống chưa có nguồn tỷ giá cấu hình nên không thể tự đề xuất bút toán chênh lệch tỷ giá (515/635). Vui lòng nhập bút toán thủ công.
                </p>
              );
            }
            if (receiptMatch && ["UNKNOWN_PAYER", "CUSTOMER_NO_OPEN_RECEIVABLE", "REVERSED_TRANSACTION"].includes(receiptMatch.outcome)) {
              return (
                <p className="text-sm text-[var(--color-muted-fg)]">
                  Tình huống này không có bút toán mặc định — cần kế toán công nợ xử lý tra soát trước.
                </p>
              );
            }
            if (receiptMatch && !v.paymentMethod) {
              return (
                <p className="text-sm text-amber-700">
                  Chưa xác định hình thức thanh toán (tiền mặt/chuyển khoản) trên chứng từ — hệ thống không tự đoán, cần nhập bút toán thủ công.
                </p>
              );
            }
            return (
              <p className="text-sm text-[var(--color-muted-fg)]">
                Chưa có bút toán. AI sẽ suggest khi bạn quay lại trang sau vài giây, hoặc chạy lại từ trang upload.
              </p>
            );
          })()
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
                {splitJournal(detail.journalSuggestions).suggestions.map((e) => (
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
                        <span className="text-emerald-700">✓ Đã chấp nhận (gợi ý)</span>
                      ) : e.rejectedAt ? (
                        <span className="text-red-700">
                          {e.rejectReason === "superseded_by_recognition" || e.rejectReason === "superseded_by_posting" ? "Thay bằng bút toán đã ghi sổ" : "✗ Đã từ chối"}
                        </span>
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
