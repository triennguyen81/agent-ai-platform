using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aiq.Gateway.Payments;

namespace Aiq.Gateway.Banking;

// ============================================================================
// Bank statement -> individual transaction lines.
//
// Structured sources (CSV / XLSX exports) are parsed deterministically from
// their columns; PDF/scan statements fall back to the extractor's
// `transactions[]`. Direction ALWAYS comes from structured debit/credit (or a
// signed amount / explicit D/C column) — never from description words.
// ============================================================================

public sealed record StatementLine(
    int LineNo,
    DateOnly? Date,
    string? BankReference,
    string Description,
    decimal Debit,
    decimal Credit,
    decimal? BalanceAfter,
    string? CounterpartyAccount,
    string? CounterpartyName,
    string? CounterpartyBankCode,
    string? CounterpartyBankName,
    string Currency)
{
    public string Direction => Credit > 0m ? "money_in" : "money_out";
    public decimal Amount => Credit > 0m ? Credit : Debit;
}

public sealed record ParsedStatement(
    string Source,                  // csv | xlsx | extractor | none
    string? CompanyAccountNumber,
    string? BankName,
    IReadOnlyList<StatementLine> Lines,
    IReadOnlyList<string> Warnings);

public static class BankStatementParser
{
    private enum Col { Date, Reference, Description, Debit, Credit, Amount, DirectionFlag, Balance, CpAccount, CpName, CpBank, Currency }

    // Folded header text -> column. Longest/most specific first.
    private static readonly (string Header, Col Col)[] HeaderMap =
    {
        ("so tai khoan doi ung", Col.CpAccount), ("tai khoan doi ung", Col.CpAccount), ("tk doi ung", Col.CpAccount),
        ("stk doi ung", Col.CpAccount), ("counterparty account", Col.CpAccount), ("beneficiary account", Col.CpAccount),
        ("ten tai khoan doi ung", Col.CpName), ("ten tk doi ung", Col.CpName), ("ten doi ung", Col.CpName),
        ("ten doi tac", Col.CpName), ("doi tac", Col.CpName), ("counterparty name", Col.CpName), ("counterparty", Col.CpName),
        ("ten nguoi chuyen", Col.CpName), ("ten nguoi nhan", Col.CpName),
        ("ngan hang doi ung", Col.CpBank), ("ma ngan hang doi ung", Col.CpBank), ("counterparty bank", Col.CpBank),
        ("ngay giao dich", Col.Date), ("ngay hach toan", Col.Date), ("ngay gd", Col.Date), ("ngay", Col.Date),
        ("transaction date", Col.Date), ("value date", Col.Date), ("date", Col.Date),
        ("ma giao dich", Col.Reference), ("ma gd", Col.Reference), ("so tham chieu", Col.Reference), ("so but toan", Col.Reference),
        ("so ct", Col.Reference), ("reference", Col.Reference), ("transaction id", Col.Reference), ("ref", Col.Reference),
        ("noi dung", Col.Description), ("dien giai", Col.Description), ("mo ta", Col.Description), ("chi tiet", Col.Description),
        ("description", Col.Description), ("narrative", Col.Description), ("details", Col.Description),
        ("so tien ghi no", Col.Debit), ("ghi no", Col.Debit), ("tien ra", Col.Debit), ("phat sinh no", Col.Debit), ("debit", Col.Debit), ("withdrawal", Col.Debit),
        ("so tien ghi co", Col.Credit), ("ghi co", Col.Credit), ("tien vao", Col.Credit), ("phat sinh co", Col.Credit), ("credit", Col.Credit), ("deposit", Col.Credit),
        ("so du", Col.Balance), ("balance", Col.Balance),
        ("so tien", Col.Amount), ("amount", Col.Amount),
        ("tien te", Col.Currency), ("loai tien", Col.Currency), ("currency", Col.Currency), ("ccy", Col.Currency),
        ("loai", Col.DirectionFlag), ("no/co", Col.DirectionFlag), ("d/c", Col.DirectionFlag), ("dr/cr", Col.DirectionFlag),
    };

    /// <summary>Does this line look like a statement table header (>= 3 known columns incl. money)?</summary>
    public static bool LooksLikeStatementHeader(string line)
    {
        var cols = MapHeader(SplitRow(line, DetectDelimiter(line)));
        var values = cols.Values.ToHashSet();
        return values.Count >= 3 && (values.Contains(Col.Credit) || values.Contains(Col.Debit) || values.Contains(Col.Amount))
            && (values.Contains(Col.Date) || values.Contains(Col.Description));
    }

    /// <summary>Deterministic parse of CSV/TSV text (also the text form of an XLSX).</summary>
    public static ParsedStatement ParseDelimitedText(string? text, string source = "csv")
    {
        var warnings = new List<string>();
        var rawLines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int headerIndex = -1; char delim = ',';
        Dictionary<int, Col>? map = null;
        string? companyAccount = null, bankName = null;

        for (var i = 0; i < Math.Min(rawLines.Length, 40); i++)
        {
            var line = rawLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var d = DetectDelimiter(line);
            var cells = SplitRow(line, d);
            var m = MapHeader(cells);
            var vals = m.Values.ToHashSet();
            if (vals.Count >= 3 && (vals.Contains(Col.Credit) || vals.Contains(Col.Debit) || vals.Contains(Col.Amount)))
            {
                headerIndex = i; delim = d; map = m; break;
            }
            // Preamble: "Số tài khoản: 0011..." / "Ngân hàng: VCB" belong to MY_COMPANY's account.
            var folded = CashSettlementRules.FoldVietnamese(string.Join(" ", cells));
            var acct = Regex.Match(folded, @"(so tai khoan|tai khoan|account( number| no)?)\s*[:\-]?\s*([0-9][0-9 .\-]{3,})");
            if (companyAccount is null && acct.Success && !folded.Contains("doi ung"))
                companyAccount = BankAccountNormalizer.NormalizeAccount(acct.Groups[3].Value);
            var bank = Regex.Match(string.Join(" ", cells), @"(?i)(Ngân hàng|Ngan hang|Bank)\s*[:\-]\s*(.+)$");
            if (bankName is null && bank.Success) bankName = bank.Groups[2].Value.Trim().Trim(',', ';');
        }
        if (map is null) return new ParsedStatement("none", companyAccount, bankName, Array.Empty<StatementLine>(), new[] { "Không tìm thấy dòng tiêu đề cột sao kê" });

        string? Cell(string[] cells, Col col) =>
            map.Where(kv => kv.Value == col && kv.Key < cells.Length).Select(kv => cells[kv.Key].Trim())
               .FirstOrDefault(v => v.Length > 0);

        var lines = new List<StatementLine>();
        for (var i = headerIndex + 1; i < rawLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(rawLines[i])) continue;
            var cells = SplitRow(rawLines[i], delim);
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var desc = Cell(cells, Col.Description) ?? "";
            var folded = CashSettlementRules.FoldVietnamese(desc + " " + (Cell(cells, Col.Date) ?? ""));
            if (Regex.IsMatch(folded, @"^(tong|cong|total|so du dau|so du cuoi|opening|closing)")) continue; // summary rows

            var debit = ParseAmount(Cell(cells, Col.Debit)) ?? 0m;
            var credit = ParseAmount(Cell(cells, Col.Credit)) ?? 0m;
            if (debit == 0m && credit == 0m && ParseAmount(Cell(cells, Col.Amount)) is { } signed && signed != 0m)
            {
                var flag = CashSettlementRules.FoldVietnamese(Cell(cells, Col.DirectionFlag));
                var isCredit = flag.Length > 0
                    ? flag.StartsWith("c") || flag.Contains("co") || flag.Contains("in")
                    : signed > 0m;
                if (isCredit) credit = Math.Abs(signed); else debit = Math.Abs(signed);
            }
            debit = Math.Abs(debit); credit = Math.Abs(credit);
            var lineNo = lines.Count + 1;
            if (debit > 0m && credit > 0m)
            {
                warnings.Add($"Dòng {i + 1}: có cả ghi nợ và ghi có — bỏ qua, cần kiểm tra file gốc");
                continue;
            }
            if (debit == 0m && credit == 0m) continue;

            lines.Add(new StatementLine(
                lineNo,
                ParseDate(Cell(cells, Col.Date)),
                Cell(cells, Col.Reference),
                desc,
                debit, credit,
                ParseAmount(Cell(cells, Col.Balance)),
                NullIfEmpty(Cell(cells, Col.CpAccount)),
                NullIfEmpty(Cell(cells, Col.CpName)),
                BankCodeOf(Cell(cells, Col.CpBank)),
                NullIfEmpty(Cell(cells, Col.CpBank)),
                NullIfEmpty(Cell(cells, Col.Currency))?.ToUpperInvariant() ?? "VND"));
        }
        return new ParsedStatement(source, companyAccount, bankName, lines, warnings);
    }

    /// <summary>PDF/scan path: normalize the extractor's transactions[] (structured debit/credit only).</summary>
    public static ParsedStatement FromExtractedFields(IReadOnlyDictionary<string, object?> fields)
    {
        var warnings = new List<string>();
        var lines = new List<StatementLine>();
        string? S(IReadOnlyDictionary<string, object?> d, params string[] keys)
        {
            foreach (var k in keys)
                if (d.TryGetValue(k, out var v) && v is not null && v.ToString() is { Length: > 0 } s) return s.Trim();
            return null;
        }
        decimal? D(IReadOnlyDictionary<string, object?> d, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!d.TryGetValue(k, out var v) || v is null) continue;
                if (v is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
                if (v is decimal m) return m;
                if (v is double db) return (decimal)db;
                if (v is long l) return l;
                if (v is int n) return n;
                if (ParseAmount(v.ToString()) is { } p) return p;
            }
            return null;
        }

        var rows = fields.TryGetValue("transactions", out var t) && t is List<object?> list
            ? list.OfType<Dictionary<string, object?>>().ToList()
            : new List<Dictionary<string, object?>>();
        foreach (var r in rows)
        {
            var debit = Math.Abs(D(r, "debit", "money_out", "tien_ra", "withdrawal") ?? 0m);
            var credit = Math.Abs(D(r, "credit", "money_in", "tien_vao", "deposit") ?? 0m);
            if (debit > 0m && credit > 0m) { warnings.Add($"Giao dịch {lines.Count + 1}: có cả ghi nợ và ghi có — bỏ qua"); continue; }
            if (debit == 0m && credit == 0m) continue;
            lines.Add(new StatementLine(
                lines.Count + 1,
                ParseDate(S(r, "date", "transaction_date", "ngay")),
                S(r, "reference", "transaction_id", "bank_reference", "ref"),
                S(r, "description", "content", "noi_dung") ?? "",
                debit, credit,
                D(r, "balance", "balance_after", "so_du"),
                S(r, "counterparty_account", "counterparty_account_number"),
                S(r, "counterparty_name"),
                BankCodeOf(S(r, "counterparty_bank_code", "counterparty_bank")),
                S(r, "counterparty_bank", "counterparty_bank_name"),
                (S(r, "currency") ?? S(fields, "currency") ?? "VND").ToUpperInvariant()));
        }
        return new ParsedStatement(lines.Count > 0 ? "extractor" : "none",
            S(fields, "account_number") is { } a ? BankAccountNormalizer.NormalizeAccount(a) : null,
            S(fields, "bank_name"), lines, warnings);
    }

    // ------------------------------------------------------------------
    // XLSX -> tab-separated text (first worksheet). No external package:
    // an .xlsx is a zip of XML parts.
    // ------------------------------------------------------------------
    public static string XlsxToText(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
        {
            using var s = ss.Open();
            shared = XDocument.Load(s).Root!.Elements(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(x => x.Value))).ToList();
        }
        var sheet = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("Không có worksheet trong file Excel");
        using var stream = sheet.Open();
        var sb = new StringBuilder();
        foreach (var row in XDocument.Load(stream).Descendants(ns + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var c in row.Elements(ns + "c"))
            {
                var col = ColumnIndex((string?)c.Attribute("r") ?? "");
                var type = (string?)c.Attribute("t");
                var raw = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Select(x => x.Value));
                var value = type == "s" && int.TryParse(raw, out var idx) && idx < shared.Count ? shared[idx] : raw;
                cells[col < 0 ? cells.Count : col] = value.Replace('\t', ' ').Replace('\n', ' ');
            }
            if (cells.Count == 0) continue;
            var max = cells.Keys.Max();
            sb.AppendLine(string.Join('\t', Enumerable.Range(0, max + 1).Select(i => cells.TryGetValue(i, out var v) ? v : "")));
        }
        return sb.ToString();
    }

    private static int ColumnIndex(string cellRef)
    {
        var letters = new string(cellRef.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length == 0) return -1;
        var n = 0;
        foreach (var ch in letters) n = n * 26 + (ch - 'A' + 1);
        return n - 1;
    }

    // ------------------------------------------------------------------
    // Cell helpers
    // ------------------------------------------------------------------

    private static char DetectDelimiter(string line)
    {
        var counts = new[] { '\t', ';', ',', '|' }.Select(d => (d, n: line.Count(c => c == d))).OrderByDescending(x => x.n).First();
        return counts.n > 0 ? counts.d : ',';
    }

    private static string[] SplitRow(string line, char delim)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == delim && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells.Select(c => c.Trim()).ToArray();
    }

    private static Dictionary<int, Col> MapHeader(string[] cells)
    {
        var map = new Dictionary<int, Col>();
        for (var i = 0; i < cells.Length; i++)
        {
            var f = Regex.Replace(CashSettlementRules.FoldVietnamese(cells[i]), @"[()\[\]:*.]", " ").Trim();
            f = Regex.Replace(f, @"\s+", " ");
            if (f.Length == 0 || f.Length > 40) continue;
            foreach (var (header, col) in HeaderMap)
            {
                if (f == header || f.StartsWith(header + " ", StringComparison.Ordinal))
                {
                    if (!map.ContainsValue(col)) map[i] = col;
                    break;
                }
            }
        }
        return map;
    }

    /// <summary>VN/EN amounts: "20.000.000", "20,000,000", "1.234,50", "(500)", "-500 VND".</summary>
    public static decimal? ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var negative = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = Regex.Replace(s, @"[^0-9.,]", "");
        if (s.Length == 0) return null;
        var lastDot = s.LastIndexOf('.'); var lastComma = s.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
        {
            var dec = lastDot > lastComma ? '.' : ',';
            var thousands = dec == '.' ? "," : ".";
            normalized = s.Replace(thousands, "").Replace(dec, '.');
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var parts = s.Split(sep);
            normalized = parts.Length > 2 || (parts.Length == 2 && parts[1].Length == 3)
                ? s.Replace(sep.ToString(), "")                 // thousands grouping
                : s.Replace(sep, '.');                           // decimal
        }
        else normalized = s;
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? (negative ? -v : v) : null;
    }

    public static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial)); // Excel serial date
        var datePart = s.Split(' ', 'T')[0];
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yy" };
        return DateOnly.TryParseExact(datePart, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string? BankCodeOf(string? bank)
    {
        if (string.IsNullOrWhiteSpace(bank)) return null;
        var t = bank.Trim();
        // A short all-caps token is a code (VCB, ACB, TCB…); longer text is a name.
        return Regex.IsMatch(t, @"^[A-Za-z]{2,10}$") ? t.ToUpperInvariant() : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
