import { describe, expect, test } from "vitest";
import { allocationTotals, blockingReasons, initialAllocation, type StatementSettlementView } from "./statement-settlement";

const base = (over: Partial<StatementSettlementView> = {}): StatementSettlementView => ({
  line: { id: "l", voucherId: "v", txDate: "2026-10-10", description: "TT DEMO-INV-VT-001", direction: "money_in", amount: 20_000_000, bankReference: "FT1", matchStatus: "matched" },
  accountingStatus: "NOT_POSTED",
  allocationStatus: null,
  customer: { id: "c", code: "DEMO-CUS-VIETTIN", name: "Việt Tín", currentBalance: 136_600_000 },
  companyBankAccount: { id: "b", accountNumber: "0011002345678", bankName: "Vietcombank", glAccountCode: "1121" },
  openItems: [],
  invoiceMatch: {
    status: "AUTO_MATCHED",
    suggestion: { certainty: "certain", method: "invoice_reference", allocations: [{ invoiceId: "i1", documentNumber: "DEMO-INV-VT-001", outstanding: 55_000_000, applyAmount: 20_000_000, matchMethod: "invoice_reference" }], unappliedAmount: 0, reason: "" },
    explicitReferences: ["DEMO-INV-VT-001"], referenceProblems: [], message: null,
  },
  posting: null, applications: [], reversedPostings: [], reconciledWith: null, duplicates: [], proposedJournal: [], blockers: [], appliedInvoices: [],
  ...over,
});

describe("statement settlement view", () => {
  test("AUTO_MATCHED suggestion pre-fills the allocation", () => {
    expect(initialAllocation(base())).toEqual({ i1: 20_000_000 });
  });

  test("NEEDS_REVIEW never pre-fills — ADMIN must choose", () => {
    const v = base({ invoiceMatch: { ...base().invoiceMatch, status: "NEEDS_REVIEW" } });
    expect(initialAllocation(v)).toEqual({});
  });

  test("allocation totals: allocated, unallocated, over-allocation", () => {
    expect(allocationTotals(50_000_000, { a: 20_000_000, b: 30_000_000 })).toEqual({ allocated: 50_000_000, unallocated: 0, overAllocated: false });
    expect(allocationTotals(50_000_000, { a: 40_000_000 })).toEqual({ allocated: 40_000_000, unallocated: 10_000_000, overAllocated: false });
    expect(allocationTotals(20_000_000, { a: 25_000_000 }).overAllocated).toBe(true);
  });

  test("only a probable duplicate can be overridden by explicit ADMIN confirmation", () => {
    const v = base({ blockers: ["possible_duplicate"] });
    expect(blockingReasons(v, false)).toEqual(["possible_duplicate"]);
    expect(blockingReasons(v, true)).toEqual([]);
    expect(blockingReasons(base({ blockers: ["already_recorded_elsewhere"] }), true)).toEqual(["already_recorded_elsewhere"]);
    expect(blockingReasons(base({ blockers: ["company_bank_account_not_linked"] }), true)).toEqual(["company_bank_account_not_linked"]);
  });
});
