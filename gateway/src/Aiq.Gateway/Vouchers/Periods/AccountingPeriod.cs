using System.Globalization;

namespace Aiq.Gateway.Vouchers.Periods;

/// <summary>
/// Chứng từ flow v2 PR4 (docs/flows/chung-tu-proposed.md §10.1, QĐ 12): an
/// accounting period is one calendar month of the accounting date. Written and
/// read as <c>yyyy-MM</c>.
/// </summary>
public readonly record struct AccountingPeriod(int Year, int Month) : IComparable<AccountingPeriod>
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);
    public AccountingPeriod Previous => Of(FirstDay.AddMonths(-1));
    public AccountingPeriod Next => Of(FirstDay.AddMonths(1));

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;

    public static AccountingPeriod Of(DateOnly date) => new(date.Year, date.Month);

    /// <summary>The period an instant falls in, on the Vietnam calendar (P9).</summary>
    public static AccountingPeriod Of(DateTimeOffset instant) => Of(VietnamTime.DateOf(instant));

    /// <summary>P1: the accounting date when set, otherwise the document date.</summary>
    public static DateOnly? EffectiveDate(DateOnly? accountingDate, DateOnly? documentDate) => accountingDate ?? documentDate;

    public static bool TryParse(string? text, out AccountingPeriod period)
    {
        period = default;
        if (text is not { Length: 7 } || text[4] != '-'
            || !int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(text.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || year is < MinYear or > MaxYear || month is < 1 or > 12) return false;
        period = new AccountingPeriod(year, month);
        return true;
    }

    public int CompareTo(AccountingPeriod other) => (Year, Month).CompareTo((other.Year, other.Month));
    public static bool operator <(AccountingPeriod a, AccountingPeriod b) => a.CompareTo(b) < 0;
    public static bool operator >(AccountingPeriod a, AccountingPeriod b) => a.CompareTo(b) > 0;
    public static bool operator <=(AccountingPeriod a, AccountingPeriod b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AccountingPeriod a, AccountingPeriod b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}

/// <summary>Asia/Ho_Chi_Minh calendar for "today" and instants (QĐ 12, QĐ 25).</summary>
public static class VietnamTime
{
    // Asia/Ho_Chi_Minh from the tz database; a fixed UTC+7 zone when the image
    // has none (Vietnam has had no DST since 1975, so the two agree).
    public static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Ho_Chi_Minh", TimeSpan.FromHours(7), "Asia/Ho_Chi_Minh", "ICT");
    }

    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    public static DateOnly Today(TimeProvider time) => DateOf(time.GetUtcNow());
}

public static class AccountingPeriodRules
{
    /// <summary>
    /// P3, for PR5/PR6: the accounting date of a reversal. A voucher whose period
    /// is open is reversed in that period (today when it is the current one).
    /// A voucher of a locked period is reversed in the nearest open period after
    /// it: today when that is the current period, otherwise its first day.
    /// </summary>
    public static DateOnly ReversalDate(DateOnly voucherDate, Func<AccountingPeriod, bool> isClosed, DateOnly today)
    {
        var period = AccountingPeriod.Of(voucherDate);
        var current = AccountingPeriod.Of(today);
        if (!isClosed(period)) return period == current ? today : voucherDate;
        do period = period.Next; while (isClosed(period));
        return period == current ? today : period.FirstDay;
    }
}
