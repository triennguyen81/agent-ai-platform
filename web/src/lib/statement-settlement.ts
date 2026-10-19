// View model for GET /vouchers/{id}/bank-transactions/{txId}/settlement — a
// bank statement money_in line settled through the canonical cash settlement.

import type { ContractScheduleImpactView } from "./contract-schedule";

export interface OpenItemView {
  id: string; documentNumber: string; documentDate: string; dueDate: string | null;
  originalAmount: number; paidAmount: number; outstanding: number; settlementStatus: string;
}
export interface AllocationSuggestionView { invoiceId: string; documentNumber: string; outstanding: number; applyAmount: number; matchMethod: string }
export interface PostingView {
  id: string; amount: number; transactionDate: string; cashAccount: string; counterAccount: string; documentNumber: string;
  status: string; postedAt: string; appliedAmount: number; unappliedAmount: number; documentReference: string | null;
  sourceType: string; bankReference: string | null; voucherId?: string;
}
export interface ApplicationView {
  id: string; cashTransactionId: string; invoiceId: string; invoiceDocumentNumber: string; appliedAmount: number;
  matchMethod: string; status: string; appliedAt: string; reversedAt: string | null; reversalReason: string | null;
}
export interface DuplicateView {
  cashTransactionId: string; kind: "exact" | "probable" | string; sourceType: string; voucherId: string;
  documentNumber: string; transactionDate: string; amount: number; documentReference: string | null; reason: string;
}
export interface StatementSettlementView {
  line: { id: string; voucherId: string; txDate: string; description: string; direction: string; amount: number; bankReference: string | null; matchStatus: string | null };
  accountingStatus: "NOT_POSTED" | "POSTED" | "RECONCILED" | "REVERSED" | string;
  allocationStatus: "UNAPPLIED" | "PARTIALLY_APPLIED" | "FULLY_APPLIED" | null | string;
  customer: { id: string; code: string; name: string; currentBalance: number } | null;
  companyBankAccount: { id: string; accountNumber: string; bankName: string; glAccountCode: string } | null;
  openItems: OpenItemView[];
  invoiceMatch: {
    status: "AUTO_MATCHED" | "NEEDS_REVIEW" | "NO_MATCH" | string;
    suggestion: { certainty: string; method: string | null; allocations: AllocationSuggestionView[]; unappliedAmount: number; reason: string } | null;
    explicitReferences: string[];
    referenceProblems: { reference: string; problem: string; ownerName: string | null; status: string | null }[];
    message: string | null;
  };
  posting: PostingView | null;
  applications: ApplicationView[];
  reversedPostings: PostingView[];
  reconciledWith: PostingView | null;
  duplicates: DuplicateView[];
  proposedJournal: { debitAccount: string; creditAccount: string; amount: number; description: string }[];
  blockers: string[];
  appliedInvoices: { invoiceId: string; documentNumber: string; originalAmount: number; paidAmount: number; outstanding: number; settlementStatus: string }[];
  contractSchedules?: ContractScheduleImpactView[];
}

export const ACCOUNTING_LABEL: Record<string, string> = {
  NOT_POSTED: "Chưa ghi nhận",
  POSTED: "ĐÃ GHI NHẬN",
  RECONCILED: "ĐÃ ĐỐI CHIẾU",
  REVERSED: "ĐÃ ĐẢO",
};

export const ALLOCATION_LABEL: Record<string, string> = {
  UNAPPLIED: "Chưa phân bổ",
  PARTIALLY_APPLIED: "Phân bổ một phần",
  FULLY_APPLIED: "Đã phân bổ hết",
};

export const INVOICE_MATCH_LABEL: Record<string, string> = {
  AUTO_MATCHED: "Đề xuất khớp hoá đơn",
  NEEDS_REVIEW: "Cần ADMIN chọn hoá đơn",
  NO_MATCH: "Khách hàng không có công nợ mở",
};

export const BLOCKER_LABEL: Record<string, string> = {
  not_money_in: "Giao dịch tiền ra — chưa hỗ trợ đối trừ công nợ phải trả.",
  customer_not_matched: "Chưa xác định khách hàng (cần MATCHED hoặc ADMIN chọn khách hàng).",
  company_bank_account_not_linked: "Chưa liên kết tài khoản ngân hàng của công ty.",
  company_bank_account_not_bank_gl: "Tài khoản ngân hàng của công ty chưa gắn TK kế toán 112x.",
  foreign_currency_not_supported: "Chưa hỗ trợ ngoại tệ.",
  already_recorded_elsewhere: "Đã ghi nhận kế toán (cùng mã giao dịch ngân hàng).",
  possible_duplicate: "Có giao dịch đã ghi nhận có thể trùng.",
};

/** Initial ADMIN allocation: only an AUTO_MATCHED suggestion is pre-filled. */
export function initialAllocation(v: StatementSettlementView): Record<string, number> {
  const out: Record<string, number> = {};
  if (v.invoiceMatch.status === "AUTO_MATCHED") for (const a of v.invoiceMatch.suggestion?.allocations ?? []) out[a.invoiceId] = a.applyAmount;
  return out;
}

export function allocationTotals(available: number, selection: Record<string, number>) {
  const allocated = Object.values(selection).reduce((s, n) => s + (n > 0 ? n : 0), 0);
  return { allocated, unallocated: available - allocated, overAllocated: allocated > available };
}

/** Blockers that stop "Duyệt & ghi nhận"; possible_duplicate can be overridden by an explicit ADMIN confirmation. */
export function blockingReasons(v: StatementSettlementView, confirmNotDuplicate: boolean): string[] {
  return v.blockers.filter((b) => !(b === "possible_duplicate" && confirmNotDuplicate));
}
