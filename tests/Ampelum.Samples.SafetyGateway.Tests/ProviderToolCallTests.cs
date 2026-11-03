using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.ProviderAdapters;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class ProviderToolCallTests
{
    // The same four requests, written the way each provider sends them.
    internal const string OpenAiJson = """
        [{"id":"c1","type":"function","function":{"name":"search","arguments":"{\"query\":\"Payroll\",\"credential\":\"directory_token\"}"}},
         {"id":"c2","type":"function","function":{"name":"drop_database","arguments":"{}"}},
         {"id":"c3","type":"function","function":{"name":"run_shell","arguments":"{\"command\":\"df -h\"}"}},
         {"id":"c4","type":"function","function":{"name":"write_file","arguments":"{\"name\":\"a\",\"content\":\"b\"}"}}]
        """;

    const string AnthropicJson = """
        [{"type":"text","text":"Let me look."},
         {"type":"tool_use","id":"c1","name":"search","input":{"query":"Payroll","credential":"directory_token"}},
         {"type":"tool_use","id":"c2","name":"drop_database","input":{}},
         {"type":"tool_use","id":"c3","name":"run_shell","input":{"command":"df -h"}},
         {"type":"tool_use","id":"c4","name":"write_file","input":{"name":"a","content":"b"}}]
        """;

    const string GeminiJson = """
        [{"text":"Let me look."},
         {"functionCall":{"name":"search","args":{"query":"Payroll","credential":"directory_token"}}},
         {"functionCall":{"name":"drop_database","args":{}}},
         {"functionCall":{"name":"run_shell","args":{"command":"df -h"}}},
         {"functionCall":{"name":"write_file","args":{"name":"a","content":"b"}}}]
        """;

    [Theory]
    [InlineData(ProviderFormat.OpenAi, OpenAiJson)]
    [InlineData(ProviderFormat.Anthropic, AnthropicJson)]
    [InlineData(ProviderFormat.Gemini, GeminiJson)]
    public async Task EveryProviderFormat_GetsTheSameVerdicts(ProviderFormat format, string json)
    {
        await using var h = await Harness.StartAsync();

        var verdicts = await h.Gateway.ReviewToolCallsAsync(format, json, TestContext.Current.CancellationToken);

        verdicts.Select(v => v.Call.Name).ShouldBe(["search", "drop_database", "run_shell", "write_file"]);
        verdicts[0].Allowed.ShouldBeTrue();
        verdicts[0].Call.Arguments["credential"].ShouldBe("directory_token");
        verdicts[1].Refusal.ShouldBe(new ToolRefusal("drop_database", "gate", "tool not in registry"));
        verdicts[2].Refusal.ShouldBe(new ToolRefusal("run_shell", "gate", "denied by policy"));
        verdicts[3].Refusal.ShouldBe(new ToolRefusal("write_file", "policy", "Tool write_file is not whitelisted in mode talk."));
        // Reviewing runs nothing, but each refusal is audited.
        h.Handbook.Names.ShouldNotContain("a");
        h.Sandbox.Commands.ShouldBeEmpty();
        (await h.AuditEntriesAsync()).Count(e => e.EventType == SafetyNotes.ToolRefused).ShouldBe(3);
    }

    [Theory]
    [InlineData("moderation_key")]
    [InlineData("some_other_alias")]
    public async Task Review_RefusesAnAliasTheRunWouldRefuse(string alias)
    {
        await using var h = await Harness.StartAsync();
        var json = """[{"functionCall":{"name":"search","args":{"query":"Payroll","credential":"ALIAS"}}}]""".Replace("ALIAS", alias);

        var verdict = (await h.Gateway.ReviewToolCallsAsync(ProviderFormat.Gemini, json, TestContext.Current.CancellationToken)).Single();

        verdict.Allowed.ShouldBeFalse();
        verdict.Refusal.ShouldBe(new ToolRefusal("search", "tool", AliasRule.Reason));
    }
}
