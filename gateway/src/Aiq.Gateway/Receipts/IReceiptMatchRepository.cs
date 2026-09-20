using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Receipts;

// ============================================================================
// Luồng 02 — Phiếu thu công nợ. This is the minimum join structure needed to
// match a `receipt` voucher to a Customer, an open receivable/invoice, and
// (if linkable via tax code) a Contract payment installment — NOT a parallel
// Customer/Contract/Voucher stack. Reuses ICustomerRepository for the actual
// AR data (sim_ar_ledger/sim_customers) and reads contract_parties/
// contract_payment_schedules directly (read-only join, no new Contract
// concept). The Agent never posts here — ProposeMatchAsync only ever writes
// this table; settling the receivable/installment happens exclusively via
// ConfirmMatchAsync, which VoucherEndpoints.ApplyReceiptAsync (ADMIN-only)
// calls after its own existing, already-idempotent ledger posting.
// ============================================================================

public sealed record ReceiptMatchResult(
    Guid Id,
    Guid VoucherId,
    Guid? CustomerId,
    decimal CustomerMatchConfidence,
    string? CustomerMatchEvidence,
    Guid? ContractId,
    Guid? InstallmentId,
    string? ReceivableReference,
    string Outcome,
    decimal ReceiptAmount,
    decimal MatchedAmount,
    decimal UnallocatedAmount,
    string Currency,
    string ReviewStatus,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public interface IReceiptMatchRepository
{
    // Deterministic outcome classification + persistence. Idempotent by
    // voucher_id — re-proposing (e.g. a manual "Trích xuất lại") upserts the
    // same row rather than creating a duplicate. Never touches sim_ar_ledger,
    // sim_customers.current_ar, or contract_payment_schedules.status — those
    // only change on ConfirmMatchAsync.
    Task<ReceiptMatchResult> ProposeMatchAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        string? payerMst, decimal receiptAmount, string currency, CancellationToken ct);

    Task<ReceiptMatchResult?> GetByVoucherIdAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);

    // ADMIN-only action (enforced by the caller). Idempotent: if already
    // confirmed, returns the existing row unchanged rather than re-applying
    // the installment status transition a second time.
    Task<ReceiptMatchResult?> ConfirmMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);

    // ADMIN-only action. Only valid while still 'proposed' — nothing to
    // reject once ledger/installment state has already been confirmed.
    Task<ReceiptMatchResult?> RejectMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);
}

public sealed class ReceiptMatchRepository : IReceiptMatchRepository
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly ICustomerRepository _customers;
    private readonly Aiq.Gateway.Contracts.INotificationRepository _notifications;

    public ReceiptMatchRepository(
        NpgsqlConnectionFactory factory, ICustomerRepository customers,
        Aiq.Gateway.Contracts.INotificationRepository notifications)
    {
        _factory = factory;
        _customers = customers;
        _notifications = notifications;
    }

    private const string Cols = """
            id                          AS Id,
            voucher_id                  AS VoucherId,
            customer_id                 AS CustomerId,
            customer_match_confidence   AS CustomerMatchConfidence,
            customer_match_evidence     AS CustomerMatchEvidence,
            contract_id                 AS ContractId,
            installment_id              AS InstallmentId,
            receivable_reference        AS ReceivableReference,
            outcome                     AS Outcome,
            receipt_amount              AS ReceiptAmount,
            matched_amount              AS MatchedAmount,
            unallocated_amount          AS UnallocatedAmount,
            currency                    AS Currency,
            review_status               AS ReviewStatus,
            reviewed_by                 AS ReviewedBy,
            reviewed_at                 AS ReviewedAt,
            created_at                  AS CreatedAt,
            updated_at                  AS UpdatedAt
        """;

    public async Task<ReceiptMatchResult?> GetByVoucherIdAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ReceiptMatchResult>(new CommandDefinition(
            $"SELECT {Cols} FROM receipt_match_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
    }

    // Earliest not-yet-paid installment on a contract whose party tax code
    // matches the customer's MST — the only linkage available since
    // sim_customers has no direct FK to contracts. Same tax-code join
    // Contract's own CT.NEW counterparty check already uses.
    private async Task<(Guid ContractId, Guid InstallmentId, decimal Amount, string Description)?> FindOpenInstallmentAsync(
        Npgsql.NpgsqlConnection conn, Guid tenantId, Guid departmentId, string mst, CancellationToken ct)
    {
        return await conn.QueryFirstOrDefaultAsync<(Guid, Guid, decimal, string)?>(new CommandDefinition("""
            SELECT c.id, s.id, s.total_amount, s.description
            FROM contract_payment_schedules s
            JOIN contracts c ON c.id = s.contract_id
            JOIN contract_parties p ON p.contract_id = c.id
            WHERE c.tenant_id=@tenantId AND c.department_id=@departmentId
              AND p.tax_code=@mst AND s.status <> 'paid'
            ORDER BY s.due_date NULLS LAST, s.ordinal
            LIMIT 1
            """, new { tenantId, departmentId, mst }, cancellationToken: ct));
    }

    public async Task<ReceiptMatchResult> ProposeMatchAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        string? payerMst, decimal receiptAmount, string currency, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        Guid? customerId = null;
        decimal confidence = 0m;
        string? evidence = null;
        Guid? contractId = null;
        Guid? installmentId = null;
        string? receivableRef = null;
        string outcome;
        decimal matched = 0m;
        decimal unallocated = 0m;

        // Order matters: currency and sign are checked before any amount
        // comparison, so we never compare a foreign-currency or negative
        // amount against VND open invoices as if it were a normal receipt.
        if (!string.Equals(currency, "VND", StringComparison.OrdinalIgnoreCase))
        {
            outcome = "FOREIGN_CURRENCY";
            unallocated = receiptAmount;
        }
        else if (receiptAmount < 0m)
        {
            outcome = "REVERSED_TRANSACTION";
            unallocated = receiptAmount;
        }
        else if (string.IsNullOrWhiteSpace(payerMst))
        {
            outcome = "UNKNOWN_PAYER";
            unallocated = receiptAmount;
        }
        else
        {
            var customer = await _customers.FindByMstAsync(tenantId, departmentId, payerMst, ct);
            if (customer is null)
            {
                outcome = "UNKNOWN_PAYER";
                unallocated = receiptAmount;
            }
            else
            {
                customerId = customer.Id;
                confidence = 1.0m; // exact MST match — deterministic, not fuzzy
                evidence = $"Khớp chính xác theo mã số thuế {payerMst}";

                var report = await _customers.GetReceivablesAsync(tenantId, departmentId, customer.Id, ct);
                var openInvoices = report?.OpenInvoices ?? Array.Empty<ArLedgerRow>().ToList();
                // Remaining balance, not the original invoice amount — a
                // partially-paid invoice's true open exposure is what's
                // left (migration 045 / open-item paid_amount model).
                var totalOpen = openInvoices.Sum(i => i.Debit - i.PaidAmount);

                var installment = await FindOpenInstallmentAsync(conn, tenantId, departmentId, payerMst, ct);
                if (installment is not null)
                {
                    contractId = installment.Value.ContractId;
                    installmentId = installment.Value.InstallmentId;
                }

                if (openInvoices.Count == 0)
                {
                    unallocated = receiptAmount;
                    if (installment is not null)
                    {
                        outcome = "ADVANCE_PAYMENT";
                        receivableRef = installment.Value.Description;
                    }
                    else
                    {
                        outcome = "CUSTOMER_NO_OPEN_RECEIVABLE";
                    }
                }
                else
                {
                    receivableRef = string.Join(", ", openInvoices.Select(i => i.DocumentNumber));
                    if (receiptAmount > totalOpen)
                    {
                        outcome = "OVERPAYMENT";
                        matched = totalOpen;
                        unallocated = receiptAmount - totalOpen;
                    }
                    else if (receiptAmount == totalOpen)
                    {
                        outcome = "FULL_MATCH";
                        matched = receiptAmount;
                        unallocated = 0m;
                    }
                    else
                    {
                        outcome = "PARTIAL_PAYMENT";
                        matched = receiptAmount;
                        unallocated = 0m;
                    }
                }
            }
        }

        // Guard excludes only 'confirmed' (settled — never silently overwritten
        // by a re-extraction). A 'rejected' row IS re-proposable: ADMIN reject
        // is a "the old proposal was wrong" signal, not a final state, so a
        // later re-extraction/manual re-propose should recompute it rather
        // than getting stuck. QueryFirstOrDefaultAsync (not QuerySingleAsync)
        // because the WHERE can legitimately suppress the update (confirmed
        // row) — that must return the existing row, never throw.
        var row = await conn.QueryFirstOrDefaultAsync<ReceiptMatchResult>(new CommandDefinition($"""
            INSERT INTO receipt_match_results (
                tenant_id, department_id, voucher_id, customer_id, customer_match_confidence,
                customer_match_evidence, contract_id, installment_id, receivable_reference,
                outcome, receipt_amount, matched_amount, unallocated_amount, currency
            ) VALUES (
                @tenantId, @departmentId, @voucherId, @customerId, @confidence,
                @evidence, @contractId, @installmentId, @receivableRef,
                @outcome, @receiptAmount, @matched, @unallocated, @currency
            )
            ON CONFLICT (voucher_id) DO UPDATE SET
                customer_id = EXCLUDED.customer_id,
                customer_match_confidence = EXCLUDED.customer_match_confidence,
                customer_match_evidence = EXCLUDED.customer_match_evidence,
                contract_id = EXCLUDED.contract_id,
                installment_id = EXCLUDED.installment_id,
                receivable_reference = EXCLUDED.receivable_reference,
                outcome = EXCLUDED.outcome,
                receipt_amount = EXCLUDED.receipt_amount,
                matched_amount = EXCLUDED.matched_amount,
                unallocated_amount = EXCLUDED.unallocated_amount,
                currency = EXCLUDED.currency,
                review_status = 'proposed',
                reviewed_by = NULL,
                reviewed_at = NULL,
                updated_at = NOW()
            WHERE receipt_match_results.review_status <> 'confirmed'
            RETURNING {Cols};
            """,
            new
            {
                tenantId, departmentId, voucherId, customerId, confidence,
                evidence, contractId, installmentId, receivableRef,
                outcome, receiptAmount, matched, unallocated, currency,
            }, cancellationToken: ct));

        if (row is not null)
        {
            // R18 — event-driven notifications for the two outcomes the
            // business document flags as needing IMMEDIATE attention
            // ("Ngay"): an unrecognized payer, and a reversed transaction.
            // Fire-and-forget-safe: idempotent (same dedup key as the AR
            // overdue job), never blocks the propose response on a
            // notification failure.
            try
            {
                if (outcome == "UNKNOWN_PAYER")
                {
                    await _notifications.NotifyDepartmentAdminsAsync(
                        tenantId, departmentId, "receipt_unknown_payer", "voucher", voucherId,
                        $"Không nhận diện được người nộp — {receiptAmount:N0} {currency}",
                        "Vào hàng đợi tra soát — gửi yêu cầu tra soát ngân hàng.", ct);
                }
                else if (outcome == "REVERSED_TRANSACTION")
                {
                    await _notifications.NotifyDepartmentAdminsAsync(
                        tenantId, departmentId, "receipt_reversed_transaction", "voucher", voucherId,
                        $"Giao dịch bị hoàn sau khi đã báo Có — {receiptAmount:N0} {currency}",
                        "Đánh dấu huỷ, giữ nguyên công nợ.", ct);
                }
            }
            catch (Exception)
            {
                // Never let a notification failure break the propose call —
                // the classification itself already succeeded and committed.
            }
            return row;
        }

        // Blocked by the guard — voucher_id already exists and is confirmed.
        // Return that settled row unchanged rather than throwing.
        return await conn.QuerySingleAsync<ReceiptMatchResult>(new CommandDefinition(
            $"SELECT {Cols} FROM receipt_match_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
    }

    public async Task<ReceiptMatchResult?> ConfirmMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await conn.QueryFirstOrDefaultAsync<ReceiptMatchResult>(new CommandDefinition(
            $"SELECT {Cols} FROM receipt_match_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId FOR UPDATE",
            new { tenantId, departmentId, voucherId }, tx, cancellationToken: ct));
        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        if (existing.ReviewStatus == "confirmed")
        {
            await tx.RollbackAsync(ct);
            return existing; // idempotent — already confirmed, no-op
        }

        // Settle the matched installment (if any). Amount-independent PAID
        // vs PARTIAL: PAID only for FULL_MATCH/OVERPAYMENT (the installment
        // itself is fully covered); PARTIAL_PAYMENT/ADVANCE_PAYMENT leave it
        // PARTIAL since the installment isn't necessarily fully covered.
        if (existing.InstallmentId is { } instId)
        {
            var newStatus = existing.Outcome is "FULL_MATCH" or "OVERPAYMENT" ? "paid" : "partial";
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE contract_payment_schedules SET status=@newStatus WHERE id=@instId AND tenant_id=@tenantId AND department_id=@departmentId AND status <> 'paid'",
                new { newStatus, instId, tenantId, departmentId }, tx, cancellationToken: ct));

            // C1/C2 audit fix — recompute contract progress and activate the
            // next milestone, if any, now that this installment settled.
            if (existing.ContractId is { } cId)
            {
                // Pure recompute (SUM of paid installments' percentage
                // share), never an increment — idempotent under retry, and a
                // 'partial' installment (money arrived but not in full)
                // contributes nothing, so it can never mark full progress.
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE contracts SET progress_percent = COALESCE((
                        SELECT SUM(percentage) FROM contract_payment_schedules
                        WHERE contract_id=@cId AND tenant_id=@tenantId AND department_id=@departmentId AND status='paid'
                    ), 0)
                    WHERE id=@cId AND tenant_id=@tenantId AND department_id=@departmentId
                    """, new { cId, tenantId, departmentId }, tx, cancellationToken: ct));

                if (newStatus == "paid")
                {
                    // Ordinal-based sequencing is the one unambiguous
                    // correspondence the schema actually supports between an
                    // installment and a milestone (per the business process
                    // document's own example: paying đợt 1 activates M2).
                    // Only ever touches a milestone that is still 'pending',
                    // trigger_event-based, and has no fixed due_date yet —
                    // never overwrites a milestone with a real fixed date,
                    // and never re-fires once already 'active' (idempotent).
                    var instOrdinal = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                        "SELECT ordinal FROM contract_payment_schedules WHERE id=@instId", new { instId }, tx, cancellationToken: ct));
                    if (instOrdinal is { } ord)
                    {
                        await conn.ExecuteAsync(new CommandDefinition("""
                            UPDATE contract_milestones SET
                                status = 'active',
                                expected_date = CASE
                                    WHEN duration_months IS NOT NULL
                                    THEN (CURRENT_DATE + (duration_months::text || ' months')::interval)::date
                                    ELSE expected_date
                                END
                            WHERE contract_id=@cId AND tenant_id=@tenantId AND department_id=@departmentId
                              AND ordinal=@nextOrdinal AND status='pending' AND trigger_event IS NOT NULL AND due_date IS NULL
                            """, new { cId, tenantId, departmentId, nextOrdinal = ord + 1 }, tx, cancellationToken: ct));
                    }
                }
            }
        }

        var updated = await conn.QuerySingleAsync<ReceiptMatchResult>(new CommandDefinition($"""
            UPDATE receipt_match_results SET
                review_status='confirmed', reviewed_by=@reviewerId, reviewed_at=NOW(), updated_at=NOW()
            WHERE id=@id
            RETURNING {Cols};
            """, new { id = existing.Id, reviewerId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<ReceiptMatchResult?> RejectMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ReceiptMatchResult>(new CommandDefinition($"""
            UPDATE receipt_match_results SET
                review_status='rejected', reviewed_by=@reviewerId, reviewed_at=NOW(), updated_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId AND review_status='proposed'
            RETURNING {Cols};
            """, new { tenantId, departmentId, voucherId, reviewerId }, cancellationToken: ct));
    }
}
