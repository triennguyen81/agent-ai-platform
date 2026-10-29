import { describe, expect, test } from "vitest";
import {
  buildPartyCards,
  gatewayErrorMessage,
  overlayLiveAp,
  overlayLiveAr,
  partyTitles,
  pendingBalanceDelta,
  selectionMismatchesReference,
  snapshotPartyId,
  type LivePayables,
  type LiveReceivables,
} from "./voucher-view";

describe("party labels by voucher type", () => {
  test("receipt uses Bên nộp tiền / Bên nhận tiền", () => {
    expect(partyTitles("receipt")).toEqual(["Bên nộp tiền", "Bên nhận tiền"]);
  });
  test("payment and bank transfer use Bên trả tiền / Bên nhận tiền", () => {
    expect(partyTitles("payment")).toEqual(["Bên trả tiền", "Bên nhận tiền"]);
    expect(partyTitles("bank_transfer")).toEqual(["Bên trả tiền", "Bên nhận tiền"]);
  });
  test("invoices keep Bên bán / Bên mua", () => {
    expect(partyTitles("invoice_vat_out")).toEqual(["Bên bán", "Bên mua"]);
    expect(partyTitles("invoice_vat_in")).toEqual(["Bên bán", "Bên mua"]);
    expect(partyTitles("invoice_vat")).toEqual(["Bên bán", "Bên mua"]);
  });
  test("expense claim does not use seller/buyer vocabulary", () => {
    expect(partyTitles("expense_claim")).not.toContain("Bên bán");
  });
});

describe("buildPartyCards", () => {
  const receipt = {
    voucherType: "receipt",
    sellerName: null, sellerMst: null,
    buyerName: null, buyerMst: "0307890123",
  };

  test("receipt: payer first, company side from settlement (Company Profile)", () => {
    const [payer, payee] = buildPartyCards(receipt, {
      payer: { name: "Công ty TNHH Việt Tín", taxCode: "0307890123" },
      payee: { name: "Cong Ty TNHH Sáu Trực", taxCode: "0123456789" },
    });
    expect(payer).toMatchObject({ title: "Bên nộp tiền", name: "Công ty TNHH Việt Tín", taxCode: "0307890123" });
    expect(payee).toMatchObject({ title: "Bên nhận tiền", name: "Cong Ty TNHH Sáu Trực", taxCode: "0123456789" });
  });

  test("receipt without settlement falls back to buyer=payer / seller=payee columns", () => {
    const [payer, payee] = buildPartyCards(receipt, null);
    expect(payer.taxCode).toBe("0307890123");
    expect(payee.taxCode).toBeNull();
  });

  test("invoice cards unchanged: seller then buyer", () => {
    const [a, b] = buildPartyCards({
      voucherType: "invoice_vat_out", sellerName: "Sáu Trực", sellerMst: "0123456789", buyerName: "Việt Tín", buyerMst: "0307890123",
    });
    expect(a).toMatchObject({ title: "Bên bán", name: "Sáu Trực" });
    expect(b).toMatchObject({ title: "Bên mua", name: "Việt Tín" });
  });
});

const aging = { current: 0, bucket_0_30: 65_000_000, bucket_31_60: 0, bucket_61_90: 0, over90: 0 };
const liveAr: LiveReceivables = {
  customer: { id: "c1", customerCode: "DEMO-CUS-VIETTIN", name: "Công ty TNHH Việt Tín", creditLimit: 3_000_000_000, currentAr: 146_600_000, category: "cong_ty" },
  currentBalance131: 146_600_000,
  creditLimit: 3_000_000_000,
  creditUsedPercent: 4.9,
  openInvoices: [{ documentNumber: "DEMO-INV-VT-001", debit: 85_000_000, credit: 0, dueDate: "2026-09-03", daysOverdue: 28, outstanding: 65_000_000, settlementStatus: "OVERDUE" }],
  aging,
  overdueAmount: 65_000_000,
  lastPaymentDate: "2026-10-02",
  paymentTermDefault: 30,
};

describe("live AR/AP overlay (no stale extraction snapshot)", () => {
  test("receipt after posting: every AR figure comes from the live report", () => {
    const stale = { current_balance_131: 166_600_000, projected_balance_131: 166_600_000, risk_flags: ["has_overdue_amount"] };
    const ar = overlayLiveAr(stale, liveAr, pendingBalanceDelta("receipt", "verified", 166_600_000, 166_600_000));
    expect(ar.current_balance_131).toBe(146_600_000);
    expect(ar.projected_balance_131).toBe(146_600_000);
    expect(ar.customer.currentAr).toBe(146_600_000);
    expect(ar.open_invoices[0]).toMatchObject({ documentNumber: "DEMO-INV-VT-001", debit: 65_000_000 });
    expect(ar.risk_flags).toEqual(["has_overdue_amount"]);
  });

  test("unposted sales invoice keeps its pending delta on top of the live balance", () => {
    expect(pendingBalanceDelta("invoice_vat_out", "extracted", 100, 150)).toBe(50);
    expect(pendingBalanceDelta("invoice_vat_out", "verified", 100, 150)).toBe(0);
    expect(pendingBalanceDelta("payment", "extracted", 100, 100)).toBe(0);
    const ar = overlayLiveAr({}, liveAr, 50_000_000);
    expect(ar.projected_balance_131).toBe(196_600_000);
  });

  test("AP after payment uses live payables", () => {
    const liveAp: LivePayables = {
      supplier: { id: "s1", supplierCode: "DEMO-SUP-KIMLONG", name: "Kim Long", mst: "0200000002", country: "VN", currency: "VND", paymentTermsDays: 30, currentAp: 67_000_000 },
      currentBalance331: 67_000_000,
      openInvoices: [{ documentNumber: "TEST-PINV-2001", debit: 0, credit: 16_500_000, dueDate: null, daysOverdue: 0, outstanding: 11_000_000, settlementStatus: "PARTIAL" }],
      aging, overdueAmount: 0, lastPaymentDate: null, paymentTermDefault: 30,
    };
    const ap = overlayLiveAp({ current_balance_331: 72_500_000, projected_balance_331: 72_500_000 }, liveAp, 0);
    expect(ap.current_balance_331).toBe(67_000_000);
    expect(ap.open_invoices[0].credit).toBe(11_000_000);
  });

  test("snapshot id read from PascalCase or camelCase", () => {
    expect(snapshotPartyId({ Id: "abc" })).toBe("abc");
    expect(snapshotPartyId({ id: "def" })).toBe("def");
    expect(snapshotPartyId(undefined)).toBeNull();
  });
});

describe("document reference vs selected invoice", () => {
  const ref = {
    status: "not_applied",
    documentReferences: ["DEMO-ST-VT-001"],
    referencedInvoices: [{ documentNumber: "DEMO-ST-VT-001", originalAmount: 39_600_000, outstanding: 0, settlementStatus: "PAID" }],
    appliedInvoices: [],
  };
  test("selecting a different invoice is flagged", () => {
    expect(selectionMismatchesReference(ref, ["DEMO-INV-VT-001"])).toBe(true);
  });
  test("selecting the referenced invoice is not flagged", () => {
    expect(selectionMismatchesReference(ref, ["demo-st-vt-001"])).toBe(false);
  });
  test("no reference on document: nothing to compare", () => {
    expect(selectionMismatchesReference({ ...ref, documentReferences: [] }, ["X"])).toBe(false);
  });
});

describe("gateway error message", () => {
  test("prefers the Gateway's human message over its error code", () => {
    const body = JSON.stringify({ error: "voucher_posted", message: "Chứng từ đã ghi sổ — không thể sửa." });
    expect(gatewayErrorMessage(body)).toBe("Chứng từ đã ghi sổ — không thể sửa.");
  });
  test("falls back to error, then detail, then the raw text", () => {
    expect(gatewayErrorMessage(JSON.stringify({ error: "rawText quá ngắn" }))).toBe("rawText quá ngắn");
    expect(gatewayErrorMessage(JSON.stringify({ detail: "boom" }))).toBe("boom");
    expect(gatewayErrorMessage("plain failure")).toBe("plain failure");
  });
  test("truncates long non-JSON bodies", () => {
    expect(gatewayErrorMessage("x".repeat(500))).toHaveLength(300);
  });
});
