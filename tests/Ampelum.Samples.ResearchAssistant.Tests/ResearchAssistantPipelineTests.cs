using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Budget;
using Xunit;

namespace Ampelum.Samples.ResearchAssistant.Tests;

public sealed class ResearchAssistantPipelineTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Question_GoesThroughFourAgentsAndReturnsACitedAnswer()
    {
        using var rig = await Rig.BuildAsync();

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.Speakers.ShouldBe(["planner", "researcher", "analyst", "writer"]);
        result.Termination.ShouldBe(TerminationReason.TerminalMessage);
        result.Queries.ShouldBe(["rooftop solar yield kWh per kW per year", "home battery module usable kWh"]);
        result.Answer.ShouldContain("6600 kWh per year");
        result.Answer.ShouldContain("2 battery modules");
        result.Citations.ShouldBe(["solar-panels#1", "battery-storage#0", "wiki:Photovoltaics", "web:1"], ignoreOrder: true);
        result.UnknownCitations.ShouldBeEmpty();
        result.IsGrounded.ShouldBeTrue();
        // planner, six analyst turns (four tools plus one more calculator call, then the final answer), writer
        rig.Model.Inner.RecordedCalls.Count.ShouldBe(8);
    }

    [Fact]
    public async Task Planner_ReceivesTheQuestionAndTheResearcherSearchesWithItsQueries()
    {
        using var rig = await Rig.BuildAsync();

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        rig.PromptOfCall(0).ShouldContain("6 kW rooftop solar array");
        result.PassageIds.ShouldContain("solar-panels#1");
        result.PassageIds.ShouldContain("battery-storage#0");
        result.PassageIds.ShouldNotContain(id => id.StartsWith("wind-turbines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyst_ReceivesTheRetrievedPassagesNotTheWholeDocumentSet()
    {
        using var rig = await Rig.BuildAsync();

        await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        var analystPrompt = rig.PromptOfCall(1);
        analystPrompt.ShouldContain("1,100 kWh per kW peak per year");
        analystPrompt.ShouldContain("One battery module stores 10 kWh usable");
        analystPrompt.ShouldNotContain("8,760 kWh");
        analystPrompt.ShouldNotContain("feed in tariff");
    }

    [Fact]
    public async Task Analyst_UsesAllFourToolsAndTheirResultsFeedTheAnswer()
    {
        using var rig = await Rig.BuildAsync();

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.ToolCalls.Select(c => c.Name).ShouldBe(
            ["calculator", "calculator", "wikipedia", "web_search", "code_interpreter"]);
        result.ToolCalls[0].Output.ShouldBe("6600");
        result.ToolCalls[4].Argument.ShouldBe("Math.Ceiling((double)6600 / 365 / 10)");
        result.ToolCalls[4].Output.ShouldBe("2");
        // the last analyst turn is sent every tool result
        var lastAnalystTurn = rig.PromptOfCall(6);
        lastAnalystTurn.ShouldContain("Photovoltaics is the conversion of light into electricity");
        lastAnalystTurn.ShouldContain("Solar yield by region");
    }

    [Fact]
    public async Task Writer_ReadsFindingsAndPassagesFromConversationMemory()
    {
        using var rig = await Rig.BuildAsync();

        await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        var writerPrompt = rig.PromptOfCall(7);
        writerPrompt.ShouldContain("Annual output: 6600 kWh");
        writerPrompt.ShouldContain("[solar-panels#1]");
    }

    [Fact]
    public async Task MemoryTooSmallToHoldTheThread_StopsTheRun()
    {
        using var rig = await Rig.BuildAsync(options: new ResearchOptions { MemoryCapacity = 2 });

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct));

        error.Message.ShouldContain("No message from");
    }

    [Fact]
    public async Task WithoutTheSolarDocument_NothingIsCalculatedAndTheAnswerIsNotGrounded()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var name in new[] { "wind-turbines.md", "grid-connection.md" })
                File.Copy(Path.Combine(ResearchData.KnowledgeDirectory, name), Path.Combine(directory, name));
            using var rig = await Rig.BuildAsync(directory: directory);

            var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

            result.ToolCalls.ShouldBeEmpty();
            result.Citations.ShouldBeEmpty();
            result.IsGrounded.ShouldBeFalse();
            result.Answer.ShouldContain("do not contain enough information");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CitationOfAnIdNobodyHandedOut_IsReportedAsUnknown()
    {
        using var rig = await Rig.BuildAsync(model => new Rewriter(model, (sent, response) =>
            WriterAnswer(sent, response, text => text.Replace("battery-storage#0", "ghost#9", StringComparison.Ordinal))));

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.UnknownCitations.ShouldBe(["ghost#9"]);
        result.Citations.ShouldContain("solar-panels#1");
        result.IsGrounded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Fact [solar-panels#1] and [ghost#9, ghost#10].", "ghost#9,ghost#10")]
    [InlineData("Fact [solar-panels#1] and [ghost#9 ].", "ghost#9")]
    [InlineData("Fact [solar-panels#1, ghost#9].", "ghost#9")]
    public async Task UnknownIdInsideAListOrPaddedBracket_FailsGrounding(string answer, string expectedUnknown)
    {
        using var rig = await Rig.BuildAsync(model => new Rewriter(model, (sent, response) =>
            WriterAnswer(sent, response, _ => WriterJson(answer))));

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.UnknownCitations.ShouldBe(expectedUnknown.Split(','));
        result.Citations.ShouldBe(["solar-panels#1"]);
        result.IsGrounded.ShouldBeFalse();
    }

    [Fact]
    public async Task WikipediaTitleWithASpace_CanBeTheOnlyCitedSourceAndStaysGrounded()
    {
        using var rig = await Rig.BuildAsync(model => new Rewriter(model, (sent, response) =>
        {
            RewriteCall(sent, response, "wikipedia", "title", "Solar panel");
            return WriterAnswer(sent, response, _ => WriterJson("Panels turn light into power [wiki:Solar panel]."));
        }));

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.Citations.ShouldBe(["wiki:Solar panel"]);
        result.UnknownCitations.ShouldBeEmpty();
        result.IsGrounded.ShouldBeTrue();
    }

    [Fact]
    public async Task WikipediaTitleWithAComma_IsOneCitationIdAndStaysGrounded()
    {
        using var rig = await Rig.BuildAsync(model => new Rewriter(model, (sent, response) =>
        {
            RewriteCall(sent, response, "wikipedia", "title", "Paris, Texas");
            return WriterAnswer(sent, response, _ => WriterJson("A city in Texas [wiki:Paris Texas]."));
        }));

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        result.Citations.ShouldBe(["wiki:Paris Texas"]);
        result.UnknownCitations.ShouldBeEmpty();
        result.IsGrounded.ShouldBeTrue();
    }

    [Fact]
    public async Task RefusedCode_ReachesTheModelAndTheRunEndsWithoutTheBatteryFigure()
    {
        using var rig = await Rig.BuildAsync(model => new Rewriter(model, (sent, response) =>
        {
            RewriteCall(sent, response, "code_interpreter", "code", "Enumerable.Range(0,2000000000).ToArray()");
            return response;
        }));

        var result = await rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct);

        var code = result.ToolCalls.Single(c => c.Name == "code_interpreter");
        code.Output.ShouldBeNull();
        code.Error.ShouldNotBeNull().ShouldContain("Code refused");
        // the call after the refusal is sent the refusal text
        rig.PromptOfCall(6).ShouldContain("Code refused");
        result.Answer.ShouldContain("battery size could not be worked out");
        result.Answer.ShouldNotContain("battery modules");
        result.Citations.ShouldContain("solar-panels#1");
        result.IsGrounded.ShouldBeTrue();
    }

    [Fact]
    public async Task ExhaustedBudget_StopsThePipeline()
    {
        using var rig = await Rig.BuildAsync(callLimit: 3);

        await Should.ThrowAsync<BudgetExceededException>(() => rig.Pipeline.AskAsync(ResearchData.SampleQuestion, Ct));

        // the planner and the first two analyst turns used the budget; the third analyst turn never reached the model
        rig.Model.Inner.RecordedCalls.Count.ShouldBe(3);
    }

    /// <summary>Lets a test change what the fake model says before the pipeline sees it.</summary>
    sealed class Rewriter(FakeModel inner, Func<IReadOnlyList<ChatMessage>, ChatResponse, ChatResponse> rewrite) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sent = messages.ToArray();
            return rewrite(sent, await inner.GetResponseAsync(sent, options, cancellationToken));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    static ChatResponse WriterAnswer(IReadOnlyList<ChatMessage> sent, ChatResponse response, Func<string, string> change) =>
        sent[0].Text.Contains(Prompts.Write, StringComparison.Ordinal)
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, change(response.Text)))
            : response;

    static string WriterJson(string answer) => System.Text.Json.JsonSerializer.Serialize(new { answer });

    static void RewriteCall(IReadOnlyList<ChatMessage> sent, ChatResponse response, string tool, string argument, string value)
    {
        if (!sent[0].Text.Contains(Prompts.Analyse, StringComparison.Ordinal))
            return;
        foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(c => c.Name == tool))
            call.Arguments = new Dictionary<string, object?> { [argument] = value };
    }
}
