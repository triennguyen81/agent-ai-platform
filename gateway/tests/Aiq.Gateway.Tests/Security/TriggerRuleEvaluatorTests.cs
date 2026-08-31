using System.Text.Json;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class TriggerRuleEvaluatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ToolLevelGateWithNoFieldConditionMatchesEveryCallToThatTool()
    {
        var rule = Parse("""{"tool":"mcp__aiq__save_contract"}""");
        Assert.True(TriggerRuleEvaluator.Matches(rule, "mcp__aiq__save_contract", Parse("{}")));
    }

    [Fact]
    public void ToolMismatchNeverMatches()
    {
        var rule = Parse("""{"tool":"mcp__aiq__save_contract"}""");
        Assert.False(TriggerRuleEvaluator.Matches(rule, "mcp__aiq__get_contract", Parse("{}")));
    }

    [Fact]
    public void NumericGteConditionMatchesAboveThreshold()
    {
        var rule = Parse("""{"tool":"pay","field_path":"amount","op":"gte","value":50000000}""");
        Assert.True(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"amount":75000000}""")));
        Assert.False(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"amount":1000}""")));
    }

    [Fact]
    public void MissingReferencedFieldFailsClosedToMatch()
    {
        // A rule references a field the call doesn't provide at all -- treat
        // as a match (require approval) rather than silently letting it pass.
        var rule = Parse("""{"tool":"pay","field_path":"amount","op":"gte","value":50000000}""");
        Assert.True(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"other":1}""")));
    }

    [Fact]
    public void UnknownOperatorFailsClosedToMatch()
    {
        var rule = Parse("""{"tool":"pay","field_path":"amount","op":"between","value":5}""");
        Assert.True(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"amount":5}""")));
    }

    [Fact]
    public void StringEqualityConditionWorks()
    {
        var rule = Parse("""{"tool":"pay","field_path":"currency","op":"eq","value":"USD"}""");
        Assert.True(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"currency":"USD"}""")));
        Assert.False(TriggerRuleEvaluator.Matches(rule, "pay", Parse("""{"currency":"VND"}""")));
    }

    [Fact]
    public void ReadOnlyOperationWithNoMatchingGateIsUnaffected()
    {
        var rule = Parse("""{"tool":"pay","field_path":"amount","op":"gte","value":50000000}""");
        Assert.False(TriggerRuleEvaluator.Matches(rule, "mcp__aiq__get_contract", Parse("""{"contract_id":"x"}""")));
    }
}
