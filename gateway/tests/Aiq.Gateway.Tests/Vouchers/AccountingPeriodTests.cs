using Aiq.Gateway.Vouchers.Periods;

namespace Aiq.Gateway.Tests.Vouchers;

/// <summary>
/// Chứng từ flow v2 PR4 (docs/flows/chung-tu-proposed.md §10.1 P1, P3, P9):
/// a period is a calendar month of the accounting date; an instant maps to a
/// period in Asia/Ho_Chi_Minh.
/// </summary>
public sealed class AccountingPeriodTests
{
    private static AccountingPeriod P(string s) => AccountingPeriod.TryParse(s, out var p) ? p : throw new FormatException(s);

    [Theory]
    [InlineData("2026-10", 2026, 10)]
    [InlineData("2027-01", 2027, 1)]
    public void Parses_yyyy_MM(string text, int year, int month)
    {
        Assert.True(AccountingPeriod.TryParse(text, out var p));
        Assert.Equal(new AccountingPeriod(year, month), p);
        Assert.Equal(text, p.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("2026-1")]
    [InlineData("26-10")]
    [InlineData("2026/10")]
    [InlineData("1999-12")]
    [InlineData("2026-10-01")]
    public void Rejects_anything_else(string? text) => Assert.False(AccountingPeriod.TryParse(text, out _));

    [Fact]
    public void The_last_minute_of_the_month_in_Vietnam_is_still_that_month()
    {
        // 23:59 on 31/10 in Vietnam = 16:59 UTC.
        Assert.Equal(P("2026-10"), AccountingPeriod.Of(new DateTimeOffset(2026, 10, 31, 16, 59, 0, TimeSpan.Zero)));
        Assert.Equal(P("2026-10"), AccountingPeriod.Of(new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.FromHours(7))));
    }

    [Fact]
    public void Midnight_on_the_first_in_Vietnam_is_the_next_month_although_UTC_is_still_the_old_one()
    {
        // 00:00 on 01/11 in Vietnam = 17:00 UTC on 31/10.
        Assert.Equal(P("2026-11"), AccountingPeriod.Of(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal(P("2027-01"), AccountingPeriod.Of(new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_date_belongs_to_its_calendar_month()
    {
        Assert.Equal(P("2026-10"), AccountingPeriod.Of(new DateOnly(2026, 10, 31)));
        Assert.Equal(P("2026-11"), AccountingPeriod.Of(new DateOnly(2026, 11, 1)));
        Assert.True(P("2026-02").Contains(new DateOnly(2026, 2, 28)));
        Assert.False(P("2026-02").Contains(new DateOnly(2026, 3, 1)));
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), (P("2028-02").FirstDay, P("2028-02").LastDay));
    }

    [Fact]
    public void Previous_and_next_cross_the_year()
    {
        Assert.Equal(P("2025-12"), P("2026-01").Previous);
        Assert.Equal(P("2027-01"), P("2026-12").Next);
        Assert.True(P("2026-09") < P("2026-10"));
        Assert.True(P("2026-12") < P("2027-01"));
    }

    [Fact]
    public void The_accounting_date_wins_and_the_document_date_is_the_default()
    {
        Assert.Equal(new DateOnly(2026, 11, 3), AccountingPeriod.EffectiveDate(new DateOnly(2026, 11, 3), new DateOnly(2026, 10, 28)));
        Assert.Equal(new DateOnly(2026, 10, 28), AccountingPeriod.EffectiveDate(null, new DateOnly(2026, 10, 28)));
        Assert.Null(AccountingPeriod.EffectiveDate(null, null));
    }

    [Fact]
    public void Vietnam_today_turns_at_local_midnight()
    {
        Assert.Equal(new DateOnly(2026, 10, 31), VietnamTime.Today(new FixedTime(new DateTimeOffset(2026, 10, 31, 16, 59, 0, TimeSpan.Zero))));
        Assert.Equal(new DateOnly(2026, 11, 1), VietnamTime.Today(new FixedTime(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero))));
    }

    // ---------------------------------------------------- P3: reversal date (PR5)

    private static Func<AccountingPeriod, bool> Closed(params string[] periods) => p => periods.Contains(p.ToString());

    [Fact]
    public void A_reversal_in_an_open_period_stays_there()
    {
        var today = new DateOnly(2026, 10, 20);
        Assert.Equal(today, AccountingPeriodRules.ReversalDate(new DateOnly(2026, 10, 5), Closed(), today));
        // Own period still open but past: the reversal keeps the voucher's date.
        Assert.Equal(new DateOnly(2026, 9, 12), AccountingPeriodRules.ReversalDate(new DateOnly(2026, 9, 12), Closed(), today));
    }

    [Fact]
    public void A_reversal_of_a_locked_period_goes_to_the_nearest_open_one()
    {
        var today = new DateOnly(2026, 11, 20);
        // Sep locked, Oct open → first day of Oct.
        Assert.Equal(new DateOnly(2026, 10, 1), AccountingPeriodRules.ReversalDate(new DateOnly(2026, 9, 12), Closed("2026-09"), today));
        // Sep and Oct locked, Nov is the current period → today.
        Assert.Equal(today, AccountingPeriodRules.ReversalDate(new DateOnly(2026, 9, 12), Closed("2026-09", "2026-10"), today));
        // Across the year.
        Assert.Equal(new DateOnly(2027, 1, 1),
            AccountingPeriodRules.ReversalDate(new DateOnly(2026, 12, 30), Closed("2026-12"), new DateOnly(2027, 2, 3)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
