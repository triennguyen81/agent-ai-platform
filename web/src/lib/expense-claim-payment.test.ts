import { describe, expect, test } from "vitest";
import { maskAccount, payBody, sourceBlocker, splitJournal } from "./expense-claim-payment";

describe("expense claim payment view", () => {
  test("the browser only sends the chosen source — never an amount or account number", () => {
    expect(payBody({ method: "cash" })).toEqual({ paymentMethod: "cash", companyBankAccountId: null });
    expect(payBody({ method: "bank", companyBankAccountId: "b1" })).toEqual({ paymentMethod: "bank", companyBankAccountId: "b1" });
  });

  test("source hints mirror the Gateway's checks", () => {
    expect(sourceBlocker(null)?.code).toBe("payment_source_required");
    expect(sourceBlocker({ method: "bank", companyBankAccountId: null })?.code).toBe("company_bank_account_required");
    expect(sourceBlocker({ method: "bank", companyBankAccountId: "b1" })).toBeNull();
    expect(sourceBlocker({ method: "cash" })).toBeNull();
  });

  test("bank account is masked to the last 4 digits", () => {
    expect(maskAccount("Vietcombank", "0011 0023 45678")).toBe("Vietcombank ****5678");
  });

  test("a suggestion is never shown as posted", () => {
    const rows = [{ id: 1, source: "posting" }, { id: 2, source: "suggestion" }, { id: 3, source: null }, { id: 4, source: "reversal" }];
    const { posted, suggestions } = splitJournal(rows);
    expect(posted.map((r) => r.id)).toEqual([1, 4]);
    expect(suggestions.map((r) => r.id)).toEqual([2, 3]);
  });
});
