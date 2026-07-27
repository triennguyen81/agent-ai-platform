using System.Data;
using Dapper;

namespace Aiq.Gateway.Data;

/// <summary>
/// Dapper does not natively bind DateOnly parameters against Postgres DATE
/// columns — Npgsql wants a DateTime. This handler converts DateOnly ↔
/// DateTime so records can keep the more expressive DateOnly type.
///
/// Read path: Npgsql returns DateTime for DATE columns; we truncate to DateOnly.
/// Write path: We expand DateOnly to DateTime at midnight UTC.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value)
    {
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new DataException($"Cannot parse DateOnly from {value?.GetType().Name}"),
        };
    }
}

public sealed class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.HasValue ? value.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
    }

    public override DateOnly? Parse(object value)
    {
        return value switch
        {
            null => null,
            DBNull => null,
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new DataException($"Cannot parse DateOnly? from {value?.GetType().Name}"),
        };
    }
}
