// Voucher type: canonical values, labels and the classification view model
// (mirrors gateway Aiq.Gateway.Vouchers.VoucherTypeClassifier). Pure, testable.

export const AUTO_VOUCHER_TYPE = "auto";

/** Upload picker: auto-detect first (default), then manual hints. */
export const UPLOAD_TYPE_OPTIONS: { value: string; label: string }[] = [
  { value: AUTO_VOUCHER_TYPE, label: "Tự động nhận diện (khuyến nghị)" },
  { value: "invoice_vat_out", label: "Hóa đơn bán" },
  { value: "invoice_vat_in", label: "Hóa đơn mua" },
  { value: "receipt", label: "Phiếu thu" },
  { value: "payment", label: "Phiếu chi" },
  { value: "bank_transfer", label: "Ủy nhiệm chi" },
  { value: "bank_statement", label: "Sao kê ngân hàng" },
  { value: "expense_claim", label: "Đề nghị hoàn ứng" },
];

/** Types an ADMIN may set on an existing voucher. */
export const SELECTABLE_TYPES = UPLOAD_TYPE_OPTIONS.filter((o) => o.value !== AUTO_VOUCHER_TYPE);

const LABELS: Record<string, string> = {
  invoice_vat_out: "Hóa đơn bán",
  invoice_vat_in: "Hóa đơn mua",
  invoice_vat: "Hóa đơn VAT",
  receipt: "Phiếu thu",
  payment: "Phiếu chi",
  bank_transfer: "Ủy nhiệm chi",
  bank_statement: "Sao kê ngân hàng",
  expense_claim: "Đề nghị hoàn ứng",
  contract: "Hợp đồng",
  unclassified: "Chưa xác định loại",
};

export const voucherTypeLabel = (t: string | null | undefined) => (t ? LABELS[t] ?? t : "Chưa xác định loại");

export interface VoucherTypeClassificationView {
  voucher_type: string;
  detected_voucher_type: string | null;
  llm_voucher_type: string | null;
  voucher_type_uploaded_as: string | null;
  confidence: number;
  evidence: string[];
  source: "auto_detected" | "user_selected" | "extractor_corrected" | "admin_corrected" | string;
  status: "ok" | "confirm_recommended" | "needs_confirmation" | "hint_mismatch" | "conflict" | string;
  requires_confirmation: boolean;
  confirmed_by: string | null;
  confirmed_at: string | null;
}

export function parseClassification(extractedFieldsJson: string | null | undefined): VoucherTypeClassificationView | null {
  if (!extractedFieldsJson) return null;
  try {
    const parsed = JSON.parse(extractedFieldsJson) as { voucher_type_classification?: VoucherTypeClassificationView };
    return parsed.voucher_type_classification ?? null;
  } catch {
    return null;
  }
}

export type ConfidenceBand = "high" | "medium" | "low";

/** >= 0.90 high (auto-select), 0.70–0.89 medium (select + warn), < 0.70 low (manual confirmation). */
export function confidenceBand(confidence: number): ConfidenceBand {
  if (confidence >= 0.9) return "high";
  if (confidence >= 0.7) return "medium";
  return "low";
}

export const SOURCE_LABEL: Record<string, string> = {
  auto_detected: "AIQ tự nhận diện",
  user_selected: "Người dùng chọn khi tải lên",
  extractor_corrected: "AIQ sửa theo nội dung chứng từ",
  admin_corrected: "ADMIN chỉnh",
};

/** Posting is blocked until a human confirms (mirrors gateway BlocksPosting). */
export const blocksPosting = (c: VoucherTypeClassificationView | null) => !!c && c.requires_confirmation && !c.confirmed_by;

/** The two choices offered when the upload hint and detection disagree. */
export function mismatchChoices(c: VoucherTypeClassificationView | null): { keep: string; detected: string } | null {
  if (!c || c.status !== "hint_mismatch" || !c.voucher_type_uploaded_as || !c.detected_voucher_type) return null;
  if (c.voucher_type_uploaded_as === c.detected_voucher_type) return null;
  return { keep: c.voucher_type_uploaded_as, detected: c.detected_voucher_type };
}

/** Extraction still running for these voucher statuses. */
export const isPipelineRunning = (status: string) =>
  status === "uploading" || status === "processing" || status === "ocr_complete";
