using System.Data;
using Dapper;

namespace Aiq.Gateway.Data;

/// <summary>
/// Dapper does not, out of the box, materialize Postgres TEXT[] columns into
/// a positional record parameter typed `string[]` — its reflection compares
/// the column's CLR type (System.Array) against the constructor parameter
/// (System.String[]) and aborts with "matching signature required".
///
/// Registering this handler tells Dapper how to convert the boxed array
/// value to a real `string[]` so positional records with array fields work.
/// </summary>
public sealed class StringArrayTypeHandler : SqlMapper.TypeHandler<string[]>
{
    public override void SetValue(IDbDataParameter parameter, string[]? value)
    {
        parameter.Value = value ?? Array.Empty<string>();
    }

    public override string[] Parse(object value)
    {
        return value switch
        {
            string[] arr => arr,
            null         => Array.Empty<string>(),
            Array a      => a.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToArray(),
            _            => Array.Empty<string>(),
        };
    }
}
