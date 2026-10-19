// View model for GET /vouchers/{id}/expense-claim/payment. Every authoritative
// value (amount, blockers, journal, status) comes from the Gateway; the browser
// only lets the ADMIN choose the payment source.

export interface PaymentBlockerView { code: string; message: string }
export interface CompanyBankAccountView { id: string; accountNumber: string; bankName: string; glAccountCode: string; isPrimary: boolean }
export interface JournalLineView { debitAccount: string; creditAccount: string; amount: number; description: string }

export interface ExpenseClaimPaymentPostingView {
  cashTransactionId: string; amount: number; paymentMethod: string; cashAccount: string; counterAccount: string;
  bankAccountId: string | null; bankName: string | null; bankAccountNumber: string | null; documentNumber: string;
  postedAt: string; paidBy: string | null; paidByName: string | null; status: string; journal: JournalLineView[];
}

export interface ExpenseClaimRecognitionView {
  status: "unrecognized" | "recognized" | "reversed" | string;
  recognizedBy: string | null; recognizedByName: string | null; recognizedAt: string | null;
  expenseAmount: number; vatDeductible: number; advanceCleared: number; employeePayable: number;
  /** true = what "Duyệt kế toán" WOULD post (not booked); false = the posted lines. */
  isPreview: boolean;
  lines: JournalLineView[];
  blockers: PaymentBlockerView[];
}

export const RECOGNITION_LABEL: Record<string, string> = {
  unrecognized: "Chưa ghi nhận",
  recognized: "ĐÃ GHI NHẬN",
  reversed: "Đã đảo",
};

export interface ExpenseClaimPaymentView {
  voucherId: string; claimId: string; reviewStatus: string; outcome: string; paymentStatus: string;
  approvedAmount: number; advanceAmount: number; payableAmount: number; currency: string;
  employee: { id: string; code: string; name: string } | null;
  taxVerificationRequired: boolean; taxGate: string | null;
  blockers: PaymentBlockerView[]; cashAccount: string; companyBankAccounts: CompanyBankAccountView[];
  payment: ExpenseClaimPaymentPostingView | null;
  recognition: ExpenseClaimRecognitionView;
}

export const APPROVAL_LABEL: Record<string, string> = {
  proposed: "Chờ duyệt bộ phận",
  dept_reviewed: "Đã duyệt bộ phận — chờ duyệt kế toán & ghi nhận",
  accounting_reviewed: "Đã duyệt",
  confirmed: "Đã duyệt",
  rejected: "Đã từ chối",
};

export const PAYMENT_LABEL: Record<string, string> = {
  unpaid: "Chưa thanh toán",
  paid: "Đã thanh toán",
  not_due: "Không phát sinh chi",
};

/** "VCB ****5678" — display only. */
export function maskAccount(bankName: string | null | undefined, accountNumber: string | null | undefined): string {
  const digits = (accountNumber ?? "").replace(/\D/g, "");
  return `${bankName ?? ""} ****${digits.slice(-4)}`.trim();
}

export type PaymentChoice = { method: "cash" } | { method: "bank"; companyBankAccountId: string | null };

/** Request body for POST /expense-claim/pay — the source only, never an amount or account. */
export function payBody(choice: PaymentChoice) {
  return choice.method === "cash"
    ? { paymentMethod: "cash", companyBankAccountId: null }
    : { paymentMethod: "bank", companyBankAccountId: choice.companyBankAccountId };
}

/** Client-side hint mirroring the Gateway's source check (the Gateway re-checks). */
export function sourceBlocker(choice: PaymentChoice | null): PaymentBlockerView | null {
  if (!choice) return { code: "payment_source_required", message: "Chọn nguồn chi: tiền mặt hoặc tài khoản ngân hàng của công ty." };
  if (choice.method === "bank" && !choice.companyBankAccountId)
    return { code: "company_bank_account_required", message: "Chưa chọn tài khoản ngân hàng của công ty." };
  return null;
}

/** Journal rows of the voucher split by authority: AI/manual suggestions are advisory only. */
export function splitJournal<T extends { source?: string | null }>(rows: T[]): { posted: T[]; suggestions: T[] } {
  return {
    posted: rows.filter((r) => r.source === "posting" || r.source === "reversal"),
    suggestions: rows.filter((r) => !r.source || r.source === "suggestion"),
  };
}
