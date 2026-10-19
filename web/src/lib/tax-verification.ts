// View model for GET /vouchers/{id}/tax-verification. The Gateway decides
// everything (identities, fingerprint, who may verify); the browser only
// shows it and sends the ADMIN's attestation, which the Gateway re-checks
// against the voucher's current data.

export interface TaxInvoiceIdentityView {
  sellerMst: string;
  invoiceSeries: string | null;
  invoiceNumber: string;
  invoiceDate: string | null;
  totalAmount: number | null;
}

export interface TaxVerificationEventView {
  id: string;
  event: string;
  actorType: "user" | "agent" | "system" | string;
  actorId: string | null;
  actorName: string | null;
  actorRole: string | null;
  source: string | null;
  statusBefore: string | null;
  statusAfter: string | null;
  reason: string | null;
  evidenceJson: string | null;
  createdAt: string;
}

export interface TaxVerificationView {
  voucherId: string;
  voucherType: string;
  verifiable: boolean;
  requiredForPayment: boolean;
  status: string | null;
  reason: string | null;
  checkedAt: string | null;
  verifiedBy: string | null;
  verifiedByName: string | null;
  verificationSource: string | null;
  evidenceJson: string | null;
  stale: boolean;
  effectivelyVerified: boolean;
  submittedBy: string;
  invoices: TaxInvoiceIdentityView[];
  events: TaxVerificationEventView[];
  /** Latest proposal's recommendation while pending_review — never authoritative. */
  proposedRecommendation: string | null;
}

export interface TaxVerificationResponse {
  verification: TaxVerificationView;
  canConfirm: boolean;
}

export const TAX_STATUS_LABEL: Record<string, string> = {
  none: "Cần xác minh",
  pending_review: "Cần xác minh (có đề xuất)",
  verified: "Đã xác minh",
  invalid: "Không hợp lệ",
  unavailable: "Không tra cứu được",
};

/** verification_source of the authoritative state. */
export const TAX_AUTHORITY_LABEL: Record<string, string> = {
  admin_manual: "ADMIN xác nhận",
  official_tax_service: "Cổng thuế (tự động)",
  agent_proposal: "Agent đề xuất (chưa có giá trị)",
  user_proposal: "Người dùng đề xuất",
  user_report: "Người dùng báo",
  system: "Hệ thống",
};

export const TAX_EVENT_LABEL: Record<string, string> = {
  proposed: "Đề xuất xác thực",
  verified: "ADMIN xác thực",
  invalid: "Ghi nhận không hợp lệ",
  unavailable: "Không tra cứu được",
  invalidated: "Huỷ xác thực do dữ liệu hoá đơn thay đổi",
  legacy_unattested: "Xác thực cũ không có bằng chứng — chờ ADMIN",
};

export const TAX_ACTOR_LABEL: Record<string, string> = { user: "Người dùng", agent: "Agent (AI)", system: "Hệ thống" };

export const TAX_SOURCE_OPTIONS: { value: string; label: string }[] = [
  { value: "tct_portal", label: "Cổng thuế — tra cứu HĐĐT (hoadondientu.gdt.gov.vn)" },
  { value: "einvoice_provider", label: "Cổng tra cứu của nhà cung cấp hoá đơn điện tử" },
  { value: "original_document", label: "Bản gốc / hoá đơn giấy có xác nhận" },
];

export const TAX_ERROR_LABEL: Record<string, string> = {
  verified_requires_admin: "Hoá đơn đã được ADMIN xác thực — chỉ ADMIN mới thay đổi được.",
  evidence_mismatch: "Thông tin đối chiếu không khớp dữ liệu chứng từ hiện tại.",
  admin_required: "Chỉ ADMIN phòng ban được kết luận — Agent/người dùng chỉ đề xuất.",
  evidence_reason_required: "Cần lý do/bằng chứng (tối thiểu 10 ký tự).",
  recommendation_invalid: "Đề xuất không hợp lệ.",
  evidence_required: "Cần bằng chứng xác minh (nguồn, ghi chú, hoá đơn đã đối chiếu).",
  evidence_note_required: "Cần ghi chú bằng chứng đối chiếu (tối thiểu 10 ký tự).",
  evidence_attestation_required: "Cần xác nhận đã tự đối chiếu trên nguồn đã chọn.",
  evidence_source_invalid: "Nguồn đối chiếu không hợp lệ.",
  not_verifiable: "Chứng từ không có hoá đơn nào cần xác thực.",
  tax_verification_required: "Chứng từ chưa được xác minh thuế.",
  tax_verification_stale: "Dữ liệu hoá đơn đã thay đổi sau khi xác thực — cần ADMIN xác thực lại.",
};

export const TAX_FIELD_LABEL: Record<string, string> = {
  missing: "chưa được đối chiếu",
  not_on_voucher: "không có trên chứng từ",
  duplicate: "khai trùng",
  invoice_series: "ký hiệu",
  invoice_date: "ngày hoá đơn",
  total_amount: "tổng tiền",
  voucher_invoice_date_missing: "chứng từ thiếu ngày hoá đơn",
  voucher_total_missing: "chứng từ thiếu tổng tiền",
};

export const TAX_RECOMMENDATION_LABEL: Record<string, string> = {
  verified: "hợp lệ",
  invalid: "không hợp lệ",
  unavailable: "không tra cứu được",
};

/** Client-side hint for the ADMIN reject form; the Gateway enforces it. */
export function rejectBlocked(reason: string, source: string): boolean {
  return reason.trim().length < 10 || !source;
}

export function buildRejectPayload(reason: string, source: string, providerUnavailable: boolean, lookupReference: string) {
  return { reason: reason.trim(), source, providerUnavailable, lookupReference: lookupReference.trim() || null };
}

export function taxStatusKey(v: Pick<TaxVerificationView, "status" | "stale">): string {
  if (v.stale) return "none";
  return v.status ?? "none";
}

/** One editable attestation row per invoice, pre-filled from the Gateway's current identity. */
export interface AttestationRow {
  sellerMst: string;
  invoiceSeries: string;
  invoiceNumber: string;
  invoiceDate: string;
  totalAmount: string;
  lookupCode: string;
}

export function initialRows(invoices: TaxInvoiceIdentityView[]): AttestationRow[] {
  return invoices.map((i) => ({
    sellerMst: i.sellerMst ?? "",
    invoiceSeries: i.invoiceSeries ?? "",
    invoiceNumber: i.invoiceNumber,
    invoiceDate: i.invoiceDate ?? "",
    totalAmount: i.totalAmount == null ? "" : String(i.totalAmount),
    lookupCode: "",
  }));
}

export function buildConfirmPayload(source: string, note: string, portalChecked: boolean, rows: AttestationRow[]) {
  return {
    source,
    note: note.trim(),
    portalChecked,
    invoices: rows.map((r) => ({
      sellerMst: r.sellerMst.trim(),
      invoiceSeries: r.invoiceSeries.trim() || null,
      invoiceNumber: r.invoiceNumber.trim(),
      invoiceDate: r.invoiceDate || null,
      totalAmount: r.totalAmount.trim() === "" ? null : Number(r.totalAmount),
      lookupCode: r.lookupCode.trim() || null,
    })),
  };
}

/** Client-side hints only; the Gateway is the authority. */
export function confirmBlockers(note: string, portalChecked: boolean, rows: AttestationRow[]): string[] {
  const out: string[] = [];
  if (note.trim().length < 10) out.push("evidence_note_required");
  if (!portalChecked) out.push("evidence_attestation_required");
  if (rows.some((r) => !r.invoiceNumber.trim() || !r.sellerMst.trim())) out.push("evidence_mismatch");
  return out;
}

export function describeProblems(details: unknown): string[] {
  const problems = (details as { problems?: { invoice: string; field: string; expected: string | null; attested: string | null }[] } | null)?.problems ?? [];
  return problems.map((p) => {
    const field = TAX_FIELD_LABEL[p.field] ?? p.field;
    if (p.field === "missing" || p.field === "not_on_voucher" || p.field === "duplicate") return `${p.invoice}: ${field}`;
    return `${p.invoice}: ${field} — chứng từ ${p.expected ?? "∅"}, đối chiếu ${p.attested ?? "∅"}`;
  });
}

export interface ParsedEvidence {
  verification_source?: string;
  evidence_source?: string;
  checked_tax_codes?: string[];
  comment?: string;
  invoices?: { invoice_number?: string; lookup_reference?: string | null }[];
}

export function parseEvidence(json: string | null | undefined): ParsedEvidence | null {
  if (!json) return null;
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}
