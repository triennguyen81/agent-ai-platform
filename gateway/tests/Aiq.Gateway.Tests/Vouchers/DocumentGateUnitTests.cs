using Aiq.Gateway.Books;
using Aiq.Gateway.Vouchers.DocumentProcessing;

namespace Aiq.Gateway.Tests.Vouchers;

public sealed class DocumentGateUnitTests
{
    [Theory]
    [InlineData(null, new string[0], new string[0])]
    [InlineData("approved", new string[0], new string[0])]
    [InlineData("awaiting_review", new string[0], new[] { "document_review:awaiting_review" })]
    [InlineData("rejected", new string[0], new[] { "document_review:rejected" })]
    [InlineData("review_task_creation_failed", new string[0], new[] { "review_task_creation_failed" })]
    [InlineData(null, new[] { "legacy_l2_failure" }, new[] { "legacy_l2_failure" })]
    [InlineData("approved", new[] { "legacy_l2_failure", "legacy_l2_failure", "" }, new[] { "legacy_l2_failure" })]
    [InlineData("awaiting_review", new[] { "legacy_l2_failure" }, new[] { "document_review:awaiting_review", "legacy_l2_failure" })]
    public void Gate_reasons_cover_review_and_processing_blockers(string? review, string[] blockers, string[] expected) =>
        Assert.Equal(expected, DocumentAccountingGate.Reasons(review, blockers));

    [Fact]
    public void Null_blocker_array_means_no_processing_blockers() =>
        Assert.Empty(DocumentAccountingGate.Reasons("approved", null));

    public static TheoryData<Exception, string> Exceptions => new()
    {
        { new AgentRequestException("rate", 429), ExtractionFailures.ProviderRateLimited },
        { new AgentRequestException("gateway timeout", 504), ExtractionFailures.ProviderTimeout },
        { new AgentRequestException("x", 408), ExtractionFailures.ProviderTimeout },
        { new AgentRequestException("You've hit your session limit · resets 3am", 502), ExtractionFailures.ProviderSessionLimit },
        { new AgentRequestException("Claude usage limit reached", 500), ExtractionFailures.ProviderSessionLimit },
        { new AgentRequestException("rate_limit_error", 500), ExtractionFailures.ProviderRateLimited },
        { new AgentRequestException("bad gateway", 502), ExtractionFailures.ProviderUnavailable },
        { new AgentRequestException("unavailable", 503), ExtractionFailures.ProviderUnavailable },
        { new AgentRequestException("boom", 500), ExtractionFailures.AgentExecutionFailed },
        { new TaskCanceledException(), ExtractionFailures.ProviderTimeout },
        { new TimeoutException(), ExtractionFailures.ProviderTimeout },
        { new HttpRequestException("connection refused"), ExtractionFailures.ProviderUnavailable },
        { new InvalidOperationException("anything"), ExtractionFailures.AgentExecutionFailed },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void Provider_failures_map_to_safe_codes(Exception ex, string code) =>
        Assert.Equal(code, ExtractionFailures.Classify(ex));

    [Theory]
    [InlineData("You've hit your session limit · resets 3am (Asia/Saigon)", ExtractionFailures.ProviderSessionLimit)]
    [InlineData("API Error: 429 Too Many Requests", ExtractionFailures.ProviderRateLimited)]
    [InlineData("Request timed out", ExtractionFailures.ProviderTimeout)]
    [InlineData("Tôi không đọc được chứng từ này.", ExtractionFailures.OutputInvalid)]
    [InlineData("", ExtractionFailures.OutputInvalid)]
    [InlineData(null, ExtractionFailures.OutputInvalid)]
    public void Non_json_answers_map_to_safe_codes(string? answer, string code) =>
        Assert.Equal(code, ExtractionFailures.ClassifyAnswer(answer));

    [Fact]
    public void Every_classifier_code_is_allowed_by_the_migration_check()
    {
        var sql = File.ReadAllText(Path.Combine(Aiq.Gateway.Tests.Data.TaxVerificationDatabase.MigrationsDir(), "063_document_gate_hardening.sql"));
        foreach (var code in new[] { ExtractionFailures.ProviderUnavailable, ExtractionFailures.ProviderRateLimited, ExtractionFailures.ProviderSessionLimit,
                     ExtractionFailures.ProviderTimeout, ExtractionFailures.AgentExecutionFailed, ExtractionFailures.OutputInvalid,
                     DocumentProvenanceErrors.AgentUnavailable, DocumentProvenanceErrors.VersionUnavailable, DocumentProvenanceErrors.ProvenanceInvalid })
            Assert.Contains($"'{code}'", sql);
    }
}
