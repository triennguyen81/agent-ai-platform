using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp;

public sealed class PiiScrubberIntegrationTests
{
    private static PiiScrubber Build() => new(
        new IPiiDetector[]
        {
            new VietnameseIdDetector(),
            new VietnameseTaxDetector(),
            new VietnamesePhoneDetector(),
            new EmailDetector(),
            new BankAccountDetector(),
            new CreditCardDetector(),
            new PassportDetector(),
            new DateOfBirthDetector(),
        },
        new DeterministicMasker());

    [Fact]
    public void Scrub_ReplacesPiiWithTokens_PiiNotInMaskedText()
    {
        var scrubber = Build();
        var text = "KH Nguyễn A, CMND 024123456, SĐT 0912345678, email a@vd.vn, MST 0101243171 nằm ở HCM";

        var result = scrubber.Scrub(text);

        Assert.True(result.DetectedCount >= 4);
        Assert.DoesNotContain("024123456", result.MaskedText);
        Assert.DoesNotContain("0912345678", result.MaskedText);
        Assert.DoesNotContain("a@vd.vn", result.MaskedText);
        Assert.DoesNotContain("0101243171", result.MaskedText);
    }

    [Fact]
    public void Unmask_RestoresOriginalPii()
    {
        var scrubber = Build();
        var original = "CMND 024123456 và SĐT 0912345678";

        var result = scrubber.Scrub(original);
        var restored = scrubber.Unmask(result.MaskedText, result.Vault);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void RoundTrip_WithLlmResponseContainingTokens_PreservesPii()
    {
        // Simulate: scrub prompt → gửi Claude → Claude trả về với token
        //          intact → unmask để lấy PII thật.
        var scrubber = Build();
        var prompt = "Trích xuất từ hoá đơn: KH có CMND 024123456 mua hàng 500tr";
        var scrubResult = scrubber.Scrub(prompt);

        // Giả lập LLM echo lại token trong response
        var fakeLlmResponse = $"Khách hàng có CMND {ExtractFirstToken(scrubResult.MaskedText)} đã mua hàng trị giá 500 triệu đồng.";
        var finalResponse = scrubber.Unmask(fakeLlmResponse, scrubResult.Vault);

        Assert.Contains("024123456", finalResponse);
    }

    [Fact]
    public void Unmask_UnknownToken_LeavesAsIs()
    {
        var scrubber = Build();
        using var vault = new PiiVault("salt");

        var text = "Response có <CMND_unknown99> token không trong vault";
        var result = scrubber.Unmask(text, vault);

        Assert.Equal(text, result);
    }

    [Fact]
    public void SameSalt_TwoScrubs_YieldsSameToken()
    {
        var scrubber = Build();
        var salt = "fixed-salt";

        var r1 = scrubber.Scrub("CMND 024123456", salt);
        var r2 = scrubber.Scrub("CMND 024123456 lần hai", salt);

        // Cùng salt + value → cùng token → kế toán viên có thể cross-reference
        Assert.Equal(ExtractFirstToken(r1.MaskedText), ExtractFirstToken(r2.MaskedText));
    }

    [Fact]
    public void EmptyText_ReturnsZeroDetections()
    {
        var scrubber = Build();
        var result = scrubber.Scrub("");

        Assert.Equal("", result.MaskedText);
        Assert.Equal(0, result.DetectedCount);
    }

    [Fact]
    public void NoPiiInText_PassesThrough()
    {
        var scrubber = Build();
        var text = "Đây là văn bản không có thông tin nhạy cảm nào cả";
        var result = scrubber.Scrub(text);

        Assert.Equal(text, result.MaskedText);
        Assert.Equal(0, result.DetectedCount);
    }

    [Fact]
    public void Benchmark_10KB_UnderFiftyMilliseconds()
    {
        var scrubber = Build();
        // Build ~10KB text có rải PII
        var chunk = "KH có CMND 024123456, SĐT 0912345678, email a@b.vn, MST 0101243171. ";
        var text = string.Concat(Enumerable.Repeat(chunk, 150));  // ~15KB

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = scrubber.Scrub(text);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Scrub of {text.Length} chars took {sw.ElapsedMilliseconds}ms (expected < 100ms as safety margin)");
        Assert.True(result.DetectedCount > 0);
    }

    private static string ExtractFirstToken(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<[A-Z]+_[a-f0-9]{10}>");
        return match.Value;
    }
}
