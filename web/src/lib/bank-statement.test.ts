import { describe, expect, test } from "vitest";
import { MATCH_METHOD_LABEL, importNoteFrom, parseMatchEvidence } from "./bank-statement";

describe("bank statement view model", () => {
  test("parses automatic evidence and ADMIN decisions separately", () => {
    const json = JSON.stringify({
      auto: {
        status: "needs_review",
        signals: [{ method: "bank_account_exact", owner_id: "a", owner_name: "Việt Tín", confidence: 1, detail: "x" }],
        candidates: [{ method: "invoice_reference", owner_id: "b", owner_name: "Minh Long", confidence: 0.95, detail: "y" }],
        warnings: ["w"],
      },
      decisions: [{ action: "admin_selected" }],
    });
    const ev = parseMatchEvidence(json);
    expect(ev.signals).toHaveLength(1);
    expect(ev.candidates[0].owner_name).toBe("Minh Long");
    expect(ev.warnings).toEqual(["w"]);
    expect(ev.decisions).toHaveLength(1);
    expect(parseMatchEvidence("not json").signals).toEqual([]);
  });

  test("import note reports duplicates from a re-upload", () => {
    expect(importNoteFrom(JSON.stringify({ bank_statement_import: { Lines: 5, Inserted: 0, Duplicates: 5 } })))
      .toBe("Đọc 5 giao dịch: 0 mới, 5 đã nhập trước đó (không tạo trùng).");
    expect(importNoteFrom(JSON.stringify({ bank_statement_import: { Lines: 5, Inserted: 5, Duplicates: 0 } }))).toBe("Đọc 5 giao dịch.");
    expect(importNoteFrom("{}")).toBeNull();
  });

  test("labels every matcher method", () => {
    for (const m of ["bank_account_exact", "tax_code", "invoice_reference", "customer_code", "name_exact", "name_similar", "amount_hint", "conflict", "admin_selected"]) {
      expect(MATCH_METHOD_LABEL[m]).toBeTruthy();
    }
  });
});
