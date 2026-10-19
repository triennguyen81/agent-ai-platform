import { describe, expect, test } from "vitest";
import {
  AUTO_VOUCHER_TYPE,
  UPLOAD_TYPE_OPTIONS,
  blocksPosting,
  confidenceBand,
  mismatchChoices,
  parseClassification,
  voucherTypeLabel,
  type VoucherTypeClassificationView,
} from "./voucher-type";

const base: VoucherTypeClassificationView = {
  voucher_type: "payment",
  detected_voucher_type: "payment",
  llm_voucher_type: "payment",
  voucher_type_uploaded_as: null,
  confidence: 0.98,
  evidence: ['Tiêu đề OCR: "PHIẾU CHI"', 'Dòng "Đơn vị trả tiền"'],
  source: "auto_detected",
  status: "ok",
  requires_confirmation: false,
  confirmed_by: null,
  confirmed_at: null,
};

describe("upload picker", () => {
  test("auto-detect is the first (default) option", () => {
    expect(UPLOAD_TYPE_OPTIONS[0]).toEqual({ value: AUTO_VOUCHER_TYPE, label: "Tự động nhận diện (khuyến nghị)" });
  });
  test("offers every canonical manual type", () => {
    expect(UPLOAD_TYPE_OPTIONS.map((o) => o.value)).toEqual([
      "auto", "invoice_vat_out", "invoice_vat_in", "receipt", "payment", "bank_transfer", "bank_statement", "expense_claim",
    ]);
  });
});

describe("classification view", () => {
  test("confidence bands", () => {
    expect(confidenceBand(0.98)).toBe("high");
    expect(confidenceBand(0.9)).toBe("high");
    expect(confidenceBand(0.72)).toBe("medium");
    expect(confidenceBand(0.5)).toBe("low");
  });

  test("parses the persisted classification", () => {
    const json = JSON.stringify({ amount: 1, voucher_type_classification: base });
    expect(parseClassification(json)?.voucher_type).toBe("payment");
    expect(parseClassification("{}")).toBeNull();
    expect(parseClassification("not json")).toBeNull();
  });

  test("hint mismatch offers keep vs detected, and blocks posting until confirmed", () => {
    const c = { ...base, status: "hint_mismatch", voucher_type_uploaded_as: "invoice_vat_out", source: "extractor_corrected", requires_confirmation: true };
    expect(mismatchChoices(c)).toEqual({ keep: "invoice_vat_out", detected: "payment" });
    expect(blocksPosting(c)).toBe(true);
    expect(blocksPosting({ ...c, confirmed_by: "admin-1" })).toBe(false);
  });

  test("no mismatch prompt when types agree or status is ok", () => {
    expect(mismatchChoices(base)).toBeNull();
    expect(blocksPosting(base)).toBe(false);
  });

  test("labels", () => {
    expect(voucherTypeLabel("bank_transfer")).toBe("Ủy nhiệm chi");
    expect(voucherTypeLabel("unclassified")).toBe("Chưa xác định loại");
    expect(voucherTypeLabel(null)).toBe("Chưa xác định loại");
  });
});
