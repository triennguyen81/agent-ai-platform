// Pure view helpers for the voucher detail page (no React, no fetch) so the
// labelling and balance rules are unit-testable.

// ---------------------------------------------------------------------------
// Party labels by voucher type
// ---------------------------------------------------------------------------

export interface PartyValue {
  name: string | null;
  taxCode: string | null;
  address?: string | null;
}

export interface PartyCard extends PartyValue {
  title: string;
}

export interface VoucherParties {
  voucherType: string;
  sellerName?: string | null;
  sellerMst?: string | null;
  sellerAddress?: string | null;
  buyerName?: string | null;
  buyerMst?: string | null;
  buyerAddress?: string | null;
}

/** Resolved payer/payee from GET /vouchers/{id}/settlement (company side filled from the Company Profile). */
export interface SettlementPartiesView {
  payer: { name: string | null; taxCode: string | null };
  payee: { name: string | null; taxCode: string | null };
}

export const CASH_VOUCHER_TYPES = new Set(["receipt", "payment", "bank_transfer"]);

export function partyTitles(voucherType: string): [string, string] {
  switch (voucherType) {
    case "receipt":
      return ["Bên nộp tiền", "Bên nhận tiền"];
    case "payment":
    case "bank_transfer":
      return ["Bên trả tiền", "Bên nhận tiền"];
    case "expense_claim":
      return ["Nhà cung cấp trên chứng từ kèm", "Đơn vị chịu chi phí"];
    default:
      // invoice_vat_out / invoice_vat_in / invoice_vat / other documents
      return ["Bên bán", "Bên mua"];
  }
}

/**
 * Two party cards in reading order. Cash vouchers show payer then payee:
 * money flows buyer-column (payer) -> seller-column (payee); when the
 * settlement view is available its resolved parties win (it fills MY_COMPANY
 * from the trusted Company Profile instead of the OCR spelling).
 */
export function buildPartyCards(v: VoucherParties, settlement?: SettlementPartiesView | null): [PartyCard, PartyCard] {
  const [first, second] = partyTitles(v.voucherType);
  if (CASH_VOUCHER_TYPES.has(v.voucherType)) {
    const payer: PartyValue = settlement?.payer.name || settlement?.payer.taxCode
      ? { name: settlement.payer.name, taxCode: settlement.payer.taxCode, address: v.buyerAddress ?? null }
      : { name: v.buyerName ?? null, taxCode: v.buyerMst ?? null, address: v.buyerAddress ?? null };
    const payee: PartyValue = settlement?.payee.name || settlement?.payee.taxCode
      ? { name: settlement.payee.name, taxCode: settlement.payee.taxCode, address: v.sellerAddress ?? null }
      : { name: v.sellerName ?? null, taxCode: v.sellerMst ?? null, address: v.sellerAddress ?? null };
    return [{ title: first, ...payer }, { title: second, ...payee }];
  }
  return [
    { title: first, name: v.sellerName ?? null, taxCode: v.sellerMst ?? null, address: v.sellerAddress ?? null },
    { title: second, name: v.buyerName ?? null, taxCode: v.buyerMst ?? null, address: v.buyerAddress ?? null },
  ];
}

// ---------------------------------------------------------------------------
// Live AR/AP balance overlay
// ---------------------------------------------------------------------------
//
// extracted_fields.ar_context / ap_context is a SNAPSHOT taken at extraction
// time. Once money is posted (or unapplied/reversed) it is stale, so every
// balance shown on the page must come from the live receivables/payables
// report instead — the same source the settlement panel uses.

interface LiveAging {
  current: number;
  bucket_0_30: number;
  bucket_31_60: number;
  bucket_61_90: number;
  over90: number;
}

interface LiveOpenInvoice {
  documentNumber: string;
  debit: number;
  credit: number;
  dueDate: string | null;
  daysOverdue: number;
  outstanding: number;
  settlementStatus: string;
}

export interface LiveReceivables {
  customer: { id: string; customerCode: string; name: string; creditLimit: number; currentAr: number; category: string };
  currentBalance131: number;
  creditLimit: number;
  creditUsedPercent: number;
  openInvoices: LiveOpenInvoice[];
  aging: LiveAging;
  overdueAmount: number;
  lastPaymentDate: string | null;
  paymentTermDefault: number;
}

export interface LivePayables {
  supplier: {
    id: string; supplierCode: string; name: string; mst: string | null; country: string;
    currency: string; paymentTermsDays: number; currentAp: number;
  };
  currentBalance331: number;
  openInvoices: LiveOpenInvoice[];
  aging: LiveAging;
  overdueAmount: number;
  lastPaymentDate: string | null;
  paymentTermDefault: number;
}

/** Shape the existing ArContextPanel renders. */
export interface ArSnapshot {
  status: "found";
  customer: { id: string; customerCode: string; name: string; creditLimit: number; currentAr: number; category: string };
  current_balance_131: number;
  credit_limit: number;
  credit_used_percent: number;
  projected_balance_131: number;
  projected_credit_used_percent: number;
  aging: LiveAging;
  overdue_amount: number;
  last_payment_date: string | null;
  payment_term_default: number;
  open_invoices: Array<{ documentNumber: string; debit: number; dueDate: string | null; daysOverdue: number }>;
  risk_flags: string[];
  requires_l2_approval: boolean;
}

/** Shape the existing ApContextPanel renders. */
export interface ApSnapshot {
  status: "found";
  supplier: LivePayables["supplier"];
  current_balance_331: number;
  projected_balance_331: number;
  aging: LiveAging;
  overdue_amount: number;
  last_payment_date: string | null;
  payment_term_default: number;
  open_invoices: Array<{ documentNumber: string; credit: number; dueDate: string | null; daysOverdue: number }>;
  risk_flags: string[];
  requires_l2_approval: boolean;
}

const pct = (amount: number, limit: number) => (limit > 0 ? Math.round((amount / limit) * 1000) / 10 : 0);

/**
 * @param pendingDelta amount this voucher would still ADD to the balance
 *   (an unposted sales invoice); 0 for receipts/payments and for anything
 *   already posted — its effect is already inside the live balance.
 */
export function overlayLiveAr(snapshot: Partial<ArSnapshot>, live: LiveReceivables, pendingDelta: number): ArSnapshot {
  const current = live.currentBalance131;
  const projected = current + pendingDelta;
  return {
    status: "found",
    customer: live.customer,
    current_balance_131: current,
    credit_limit: live.creditLimit,
    credit_used_percent: live.creditUsedPercent,
    projected_balance_131: projected,
    projected_credit_used_percent: pct(projected, live.creditLimit),
    aging: live.aging,
    overdue_amount: live.overdueAmount,
    last_payment_date: live.lastPaymentDate,
    payment_term_default: live.paymentTermDefault,
    // Remaining (outstanding) amount, not the original invoice amount.
    open_invoices: live.openInvoices.map((i) => ({
      documentNumber: i.documentNumber, debit: i.outstanding, dueDate: i.dueDate, daysOverdue: i.daysOverdue,
    })),
    risk_flags: snapshot.risk_flags ?? [],
    requires_l2_approval: snapshot.requires_l2_approval ?? false,
  };
}

export function overlayLiveAp(snapshot: Partial<ApSnapshot>, live: LivePayables, pendingDelta: number): ApSnapshot {
  const current = live.currentBalance331;
  return {
    status: "found",
    supplier: live.supplier,
    current_balance_331: current,
    projected_balance_331: current + pendingDelta,
    aging: live.aging,
    overdue_amount: live.overdueAmount,
    last_payment_date: live.lastPaymentDate,
    payment_term_default: live.paymentTermDefault,
    open_invoices: live.openInvoices.map((i) => ({
      documentNumber: i.documentNumber, credit: i.outstanding, dueDate: i.dueDate, daysOverdue: i.daysOverdue,
    })),
    risk_flags: snapshot.risk_flags ?? [],
    requires_l2_approval: snapshot.requires_l2_approval ?? false,
  };
}

/** Customer/supplier id from an extraction snapshot (stored PascalCase by the gateway job). */
export function snapshotPartyId(entity: Record<string, unknown> | undefined | null): string | null {
  if (!entity) return null;
  const id = entity.id ?? entity.Id;
  return typeof id === "string" ? id : null;
}

/** How much an UNPOSTED voucher would still add to AR/AP (invoices only; receipts/payments never increase it). */
export function pendingBalanceDelta(voucherType: string, status: string, snapshotCurrent?: number, snapshotProjected?: number): number {
  if (status === "verified" || CASH_VOUCHER_TYPES.has(voucherType)) return 0;
  if (snapshotCurrent == null || snapshotProjected == null) return 0;
  return Math.max(0, snapshotProjected - snapshotCurrent);
}

// ---------------------------------------------------------------------------
// Document reference vs applied invoice
// ---------------------------------------------------------------------------

export interface ReferenceCheckView {
  status: "no_reference" | "not_applied" | "matched" | "mismatch" | string;
  documentReferences: string[];
  referencedInvoices: Array<{ documentNumber: string; originalAmount: number; outstanding: number; settlementStatus: string }>;
  appliedInvoices: string[];
}

const norm = (s: string) => s.toUpperCase().replace(/[^A-Z0-9]/g, "");

/** For the ADMIN's in-progress selection (before posting): does it differ from the document reference? */
export function selectionMismatchesReference(reference: ReferenceCheckView | null | undefined, selectedDocNumbers: string[]): boolean {
  if (!reference || reference.documentReferences.length === 0 || selectedDocNumbers.length === 0) return false;
  const refs = new Set([...reference.documentReferences, ...reference.referencedInvoices.map((r) => r.documentNumber)].map(norm));
  return selectedDocNumbers.some((d) => !refs.has(norm(d)));
}

// ---------------------------------------------------------------------------
// Gateway error bodies
// ---------------------------------------------------------------------------

/**
 * Human-readable text of a Gateway error body: its `message` (e.g. the 409
 * voucher_posted / duplicate_voucher refusals), else `error` / `detail`,
 * else the raw text.
 */
export function gatewayErrorMessage(text: string): string {
  try {
    const parsed = JSON.parse(text) as { message?: unknown; error?: unknown; detail?: unknown };
    for (const value of [parsed.message, parsed.error, parsed.detail]) {
      if (typeof value === "string" && value.trim()) return value;
    }
  } catch {
    // not JSON
  }
  return text.slice(0, 300);
}
