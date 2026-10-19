using System.IO.Compression;
using System.Text;
using Aiq.Gateway.Banking;

namespace Aiq.Gateway.Tests.Banking;

public sealed class BankStatementParserTests
{
    public const string DemoCsv = """
        SAO KÊ TÀI KHOẢN
        Số tài khoản: 0011 0023 45678
        Ngân hàng: Vietcombank
        Ngày,Mã GD,Nội dung,TK đối ứng,Tên đối ứng,Ngân hàng đối ứng,Tiền vào,Tiền ra,Số dư
        04/10/2026,FT26277001,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,"20,000,000",,"120,000,000"
        04/10/2026,FT26277002,MINH LONG THANH TOAN,456789123,CÔNG TY TNHH MINH LONG,VCB,"30,000,000",,"150,000,000"
        04/10/2026,FT26277003,VIET TIN THANH TOAN HD DEMO-INV-VT-001,,,,"5,000,000",,"155,000,000"
        04/10/2026,FT26277004,THANH TOAN,123888999,NGUYEN VAN A,TCB,"15,000,000",,"170,000,000"
        04/10/2026,FT26277005,THANH TOAN TIEN HANG,987654321,KIM LONG,ACB,,"5,500,000","164,500,000"
        Tổng cộng,,,,,,"70,000,000","5,500,000",
        """;

    [Fact]
    public void Csv_ParsesEveryTransactionLine_SkipsTotals()
    {
        var p = BankStatementParser.ParseDelimitedText(DemoCsv);
        Assert.Equal("csv", p.Source);
        Assert.Equal(5, p.Lines.Count);
        Assert.Equal("00110023 45678".Replace(" ", ""), p.CompanyAccountNumber);
        Assert.Equal("Vietcombank", p.BankName);
    }

    [Fact]
    public void Csv_MoneyInAndOut_FromDebitCreditColumns()
    {
        var p = BankStatementParser.ParseDelimitedText(DemoCsv);
        var first = p.Lines[0];
        Assert.Equal(("money_in", 20_000_000m, 0m, 120_000_000m), (first.Direction, first.Amount, first.Debit, first.BalanceAfter!.Value));
        var last = p.Lines[4];
        Assert.Equal(("money_out", 5_500_000m), (last.Direction, last.Amount));
        Assert.Equal(new DateOnly(2026, 10, 4), first.Date);
        Assert.Equal("FT26277001", first.BankReference);
    }

    [Fact]
    public void Csv_CounterpartyAccountNameBank_Extracted()
    {
        var l = BankStatementParser.ParseDelimitedText(DemoCsv).Lines[0];
        Assert.Equal(("123456789", "CÔNG TY TNHH VIỆT TÍN", "VCB"), (l.CounterpartyAccount, l.CounterpartyName, l.CounterpartyBankCode));
        Assert.Null(BankStatementParser.ParseDelimitedText(DemoCsv).Lines[2].CounterpartyAccount);
    }

    [Fact]
    public void Direction_NeverInferredFromDescriptionWords()
    {
        // Description says "nhận" (receive) but the money is in the debit column.
        const string csv = "Ngày;Diễn giải;Ghi nợ;Ghi có\n01/10/2026;NHAN TIEN HOAN UNG;1.000.000;\n";
        var l = Assert.Single(BankStatementParser.ParseDelimitedText(csv).Lines);
        Assert.Equal("money_out", l.Direction);
    }

    [Fact]
    public void SignedAmountColumn_AndDcFlag()
    {
        const string csv = "Date\tDescription\tAmount\tD/C\n2026-10-01\tA\t500\tC\n2026-10-01\tB\t700\tD\n2026-10-02\tC\t-300\t\n";
        var lines = BankStatementParser.ParseDelimitedText(csv).Lines;
        Assert.Equal(new[] { "money_in", "money_out", "money_out" }, lines.Select(l => l.Direction));
    }

    [Fact]
    public void RowWithBothDebitAndCredit_IsRejectedWithWarning()
    {
        const string csv = "Ngày,Nội dung,Tiền vào,Tiền ra\n01/10/2026,X,100,200\n";
        var p = BankStatementParser.ParseDelimitedText(csv);
        Assert.Empty(p.Lines);
        Assert.Single(p.Warnings);
    }

    [Theory]
    [InlineData("20.000.000", 20000000)]
    [InlineData("20,000,000", 20000000)]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("(500)", -500)]
    [InlineData("-5.500.000 VND", -5500000)]
    [InlineData("12.5", 12.5)]
    public void ParseAmount_VietnameseAndEnglishFormats(string raw, double expected) =>
        Assert.Equal((decimal)expected, BankStatementParser.ParseAmount(raw));

    [Theory]
    [InlineData("0012 3456 789", "00123456789")]
    [InlineData(" 001-234.567/89 ", "00123456789")]
    [InlineData("0000000001", "0000000001")]
    [InlineData("vcb12 34", "VCB1234")]
    public void AccountNormalization_KeepsLeadingZeros_StringOnly(string raw, string expected) =>
        Assert.Equal(expected, BankAccountNormalizer.NormalizeAccount(raw));

    [Fact]
    public void AccountNameNormalization_MatchesAccentedAndAbbreviatedForms()
    {
        Assert.Equal(BankAccountNormalizer.NormalizeName("CÔNG TY TNHH VIỆT TÍN"), BankAccountNormalizer.NormalizeName("CTY TNHH VIET TIN"));
        Assert.Equal("viet tin", BankAccountNormalizer.NormalizeName("CTY TNHH VIET TIN"));
    }

    [Fact]
    public void Xlsx_ConvertedToText_ThenParsed()
    {
        var bytes = BuildXlsx(new[]
        {
            new[] { "Ngày", "Nội dung", "TK đối ứng", "Tên đối ứng", "Tiền vào", "Tiền ra" },
            new[] { "46299", "TT DEMO-INV-VT-001", "0123456789", "VIET TIN", "20000000", "" }, // Excel serial date
        });
        var p = BankStatementParser.ParseDelimitedText(BankStatementParser.XlsxToText(bytes), "xlsx");
        var l = Assert.Single(p.Lines);
        Assert.Equal(("0123456789", 20_000_000m, "money_in"), (l.CounterpartyAccount, l.Amount, l.Direction));
        Assert.Equal(new DateOnly(2026, 10, 4), l.Date);
    }

    [Fact]
    public void ExtractorRows_PdfPath_UseStructuredDebitCredit()
    {
        var fields = new Dictionary<string, object?>
        {
            ["account_number"] = "0011 0023 45678",
            ["transactions"] = new List<object?>
            {
                new Dictionary<string, object?> { ["date"] = "2026-10-04", ["description"] = "TT", ["debit"] = 0, ["credit"] = 20000000, ["counterparty_account"] = "123456789", ["counterparty_name"] = "VIET TIN" },
                new Dictionary<string, object?> { ["date"] = "2026-10-04", ["description"] = "chi", ["debit"] = 5500000, ["credit"] = 0 },
            },
        };
        var p = BankStatementParser.FromExtractedFields(fields);
        Assert.Equal("extractor", p.Source);
        Assert.Equal(new[] { "money_in", "money_out" }, p.Lines.Select(l => l.Direction));
        Assert.Equal("00110023 45678".Replace(" ", ""), p.CompanyAccountNumber);
    }

    [Fact]
    public void StatementHeader_IsRecognised()
    {
        Assert.True(BankStatementParser.LooksLikeStatementHeader("Ngày,Mã GD,Nội dung,TK đối ứng,Tên đối ứng,Tiền vào,Tiền ra,Số dư"));
        Assert.False(BankStatementParser.LooksLikeStatementHeader("Họ tên,Phòng ban,Mã nhân viên"));
    }

    [Fact]
    public void Fingerprint_StableAcrossImports_DistinctForIdenticalTwins()
    {
        var tenant = Guid.NewGuid();
        var l = BankStatementParser.ParseDelimitedText(DemoCsv).Lines[3];
        Assert.Equal(BankStatementRepository.Fingerprint(tenant, "001", l, 0), BankStatementRepository.Fingerprint(tenant, "001", l, 0));
        var noRef = l with { BankReference = null };
        Assert.NotEqual(BankStatementRepository.Fingerprint(tenant, "001", noRef, 0), BankStatementRepository.Fingerprint(tenant, "001", noRef, 1));
        Assert.NotEqual(BankStatementRepository.Fingerprint(tenant, "001", l, 0), BankStatementRepository.Fingerprint(Guid.NewGuid(), "001", l, 0));
    }

    private static byte[] BuildXlsx(string[][] rows)
    {
        var shared = rows.SelectMany(r => r).Distinct().ToList();
        string Col(int i) => ((char)('A' + i)).ToString();
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var v = rows[r][c];
                if (v.Length == 0) continue;
                sheet.Append(decimal.TryParse(v, out _)
                    ? $"<c r=\"{Col(c)}{r + 1}\"><v>{v}</v></c>"
                    : $"<c r=\"{Col(c)}{r + 1}\" t=\"s\"><v>{shared.IndexOf(v)}</v></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        var sst = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + string.Concat(shared.Select(s => $"<si><t>{System.Security.SecurityElement.Escape(s)}</t></si>")) + "</sst>";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open())) w.Write(sst);
            using (var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open())) w.Write(sheet.ToString());
        }
        return ms.ToArray();
    }
}
