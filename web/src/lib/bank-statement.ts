// View model for bank statement lines (GET /vouchers/{id}/bank-transactions). Pure, testable.

export interface StatementTransactionView {
  id: string;
  lineNo: number | null;
  txDate: string;
  bankReference: string | null;
  description: string;
  direction: "money_in" | "money_out";
  amount: number;
  currency: string;
  counterpartyAccount: string | null;
  counterpartyName: string | null;
  counterpartyBankCode: string | null;
  customerId: string | null;
  customerName: string | null;
  supplierId: string | null;
  supplierName: string | null;
  matchStatus: "matched" | "needs_review" | "unmatched" | null;
  matchMethod: string | null;
  matchConfidence: number | null;
  matchEvidenceJson: string;
  confirmedBy: string | null;
  confirmedAt: string | null;
  counterpartyAccountKnownForOwner: boolean;
  companyBankAccountId: string | null;
  accountingStatus: "NOT_POSTED" | "POSTED" | "RECONCILED" | "REVERSED";
  cashTransactionId: string | null;
}

export const MATCH_METHOD_LABEL: Record<string, string> = {
  bank_account_exact: "Tài khoản ngân hàng",
  tax_code: "Mã số thuế",
  invoice_reference: "Số hoá đơn",
  customer_code: "Mã khách hàng",
  name_exact: "Tên pháp lý",
  name_similar: "Tên gần giống (gợi ý)",
  amount_hint: "Số tiền (chỉ gợi ý)",
  conflict: "Mâu thuẫn căn cứ",
  admin_selected: "ADMIN chọn",
};

interface Signal { method: string; owner_id: string | null; owner_name: string | null; confidence: number; detail: string }

export interface MatchEvidenceView {
  signals: Signal[];
  candidates: Signal[];
  warnings: string[];
  decisions: unknown[];
}

export function parseMatchEvidence(json: string | null | undefined): MatchEvidenceView {
  const empty: MatchEvidenceView = { signals: [], candidates: [], warnings: [], decisions: [] };
  if (!json) return empty;
  try {
    const parsed = JSON.parse(json) as { auto?: Partial<MatchEvidenceView>; decisions?: unknown[] };
    return {
      signals: parsed.auto?.signals ?? [],
      candidates: parsed.auto?.candidates ?? [],
      warnings: parsed.auto?.warnings ?? [],
      decisions: parsed.decisions ?? [],
    };
  } catch {
    return empty;
  }
}

/** Human summary of extracted_fields.bank_statement_import (serialized PascalCase by the gateway job). */
export function importNoteFrom(extractedFieldsJson: string | null | undefined): string | null {
  if (!extractedFieldsJson) return null;
  try {
    const s = (JSON.parse(extractedFieldsJson) as { bank_statement_import?: Record<string, unknown> }).bank_statement_import;
    if (!s) return null;
    const lines = Number(s.Lines ?? s.lines ?? 0);
    const inserted = Number(s.Inserted ?? s.inserted ?? 0);
    const dup = Number(s.Duplicates ?? s.duplicates ?? 0);
    return dup > 0
      ? `Đọc ${lines} giao dịch: ${inserted} mới, ${dup} đã nhập trước đó (không tạo trùng).`
      : `Đọc ${lines} giao dịch.`;
  } catch {
    return null;
  }
}
