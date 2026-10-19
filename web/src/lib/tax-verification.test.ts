import { describe, expect, test } from "vitest";
import { buildConfirmPayload, buildRejectPayload, confirmBlockers, describeProblems, initialRows, rejectBlocked, taxStatusKey } from "./tax-verification";

const inv = [{ sellerMst: "0312345678", invoiceSeries: "1C26TAA", invoiceNumber: "123", invoiceDate: "2026-09-20", totalAmount: 11_000_000 }];

describe("tax verification view", () => {
  test("a stale verification is shown as not verified", () => {
    expect(taxStatusKey({ status: "verified", stale: false })).toBe("verified");
    expect(taxStatusKey({ status: "verified", stale: true })).toBe("none");
    expect(taxStatusKey({ status: null, stale: false })).toBe("none");
  });

  test("attestation rows pre-fill from the Gateway identity; payload is what the ADMIN attests", () => {
    const rows = initialRows(inv);
    rows[0].lookupCode = " ABC123 ";
    expect(buildConfirmPayload("tct_portal", "  Đã tra cứu, hợp lệ  ", true, rows)).toEqual({
      source: "tct_portal", note: "Đã tra cứu, hợp lệ", portalChecked: true,
      invoices: [{ sellerMst: "0312345678", invoiceSeries: "1C26TAA", invoiceNumber: "123", invoiceDate: "2026-09-20", totalAmount: 11_000_000, lookupCode: "ABC123" }],
    });
  });

  test("confirm needs a note and the explicit attestation", () => {
    expect(confirmBlockers("ngắn", false, initialRows(inv))).toEqual(["evidence_note_required", "evidence_attestation_required"]);
    expect(confirmBlockers("Đã tra cứu trên cổng TCT", true, initialRows(inv))).toEqual([]);
  });

  test("Gateway mismatch details become readable lines", () => {
    expect(describeProblems({ problems: [
      { invoice: "0312345678 / 123", field: "total_amount", expected: "11000000.00", attested: "10000000.00" },
      { invoice: "0312345678|999", field: "not_on_voucher", expected: null, attested: "0312345678|999" },
    ] })).toEqual([
      "0312345678 / 123: tổng tiền — chứng từ 11000000.00, đối chiếu 10000000.00",
      "0312345678|999: không có trên chứng từ",
    ]);
    expect(describeProblems(null)).toEqual([]);
  });

  test("ADMIN reject needs a source and a real reason", () => {
    expect(rejectBlocked("ngắn", "tct_portal")).toBe(true);
    expect(rejectBlocked("Tra cứu TCT: HĐ đã huỷ", "")).toBe(true);
    expect(rejectBlocked("Tra cứu TCT: HĐ đã huỷ", "tct_portal")).toBe(false);
    expect(buildRejectPayload("  Tra cứu TCT: HĐ đã huỷ ", "tct_portal", false, " ")).toEqual({
      reason: "Tra cứu TCT: HĐ đã huỷ", source: "tct_portal", providerUnavailable: false, lookupReference: null,
    });
  });
});
