using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Tools.WebSearch;
using Xunit;

namespace Ampelum.Samples.ResearchAssistant.Tests;

public sealed class ResearchToolsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static (ResearchTools Tools, FakeWebHandler Web) Build(string searchToken = "test-token", int topK = 2)
    {
        var web = new FakeWebHandler();
        var http = new HttpClient(web);
        return (new ResearchTools(http, new BraveSearchProvider(http, searchToken), topK), web);
    }

    static async Task<string> InvokeAsync(ResearchTools tools, string tool, string argument, string value)
    {
        var function = tools.Functions.Single(f => f.Name == tool);
        var result = await function.InvokeAsync(new AIFunctionArguments { [argument] = value }, Ct);
        return result?.ToString() ?? "";
    }

    [Fact]
    public void ToolSet_HasTheFourNamedTools()
    {
        var (tools, _) = Build();

        tools.Functions.Select(f => f.Name).ShouldBe(["calculator", "wikipedia", "web_search", "code_interpreter"]);
    }

    [Fact]
    public async Task Calculator_EvaluatesTheExpressionItIsGiven()
    {
        var (tools, _) = Build();

        (await InvokeAsync(tools, "calculator", "expression", "6 * 1100")).ShouldBe("6600");
        (await InvokeAsync(tools, "calculator", "expression", "(2 + 3) * 4")).ShouldBe("20");
        tools.Calls.Select(c => c.Argument).ShouldBe(["6 * 1100", "(2 + 3) * 4"]);
    }

    [Fact]
    public async Task Calculator_ErrorIsLoggedAndRaised()
    {
        var (tools, _) = Build();

        await Should.ThrowAsync<Exception>(() => InvokeAsync(tools, "calculator", "expression", "6 * * apples"));

        tools.Calls.Single().Error.ShouldNotBeNull();
        tools.Calls.Single().Output.ShouldBeNull();
    }

    [Fact]
    public async Task Wikipedia_RequestsTheArticleAndHandsOutASourceId()
    {
        var (tools, web) = Build();

        var text = await InvokeAsync(tools, "wikipedia", "title", "Photovoltaics");

        web.Requests.Single().Uri.AbsolutePath.ShouldBe("/api/rest_v1/page/summary/Photovoltaics");
        text.ShouldStartWith("[wiki:Photovoltaics] Photovoltaics is the conversion of light into electricity");
        text.ShouldContain("https://en.wikipedia.org/wiki/Photovoltaics");
        tools.SourceIds.ShouldBe(["wiki:Photovoltaics"]);
    }

    [Fact]
    public async Task Wikipedia_UnknownArticleIsAnErrorWithoutASourceId()
    {
        var (tools, _) = Build();

        await Should.ThrowAsync<HttpRequestException>(() => InvokeAsync(tools, "wikipedia", "title", "No Such Page"));

        tools.Calls.Single().Error.ShouldNotBeNull();
        tools.SourceIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task WebSearch_SendsQueryCountAndTokenAndNumbersTheHits()
    {
        var (tools, web) = Build(topK: 2);

        var first = await InvokeAsync(tools, "web_search", "query", "solar yield Central Europe");
        await InvokeAsync(tools, "web_search", "query", "home battery sizing");

        var request = web.Requests[0];
        request.Uri.Host.ShouldBe("api.search.brave.com");
        request.Uri.Query.ShouldContain("q=solar%20yield%20Central%20Europe");
        request.Uri.Query.ShouldContain("count=2");
        request.Headers["X-Subscription-Token"].ShouldBe("test-token");
        first.ShouldStartWith("[web:1] Solar yield by region");
        // ids keep counting across calls so two searches never hand out the same id
        tools.SourceIds.ShouldContain("web:1");
        tools.SourceIds.Count.ShouldBeGreaterThan(1);
        tools.SourceIds.ShouldAllBe(id => id.StartsWith("web:", StringComparison.Ordinal));
        tools.SourceIds.Distinct().Count().ShouldBe(tools.SourceIds.Count);
    }

    [Fact]
    public async Task WebSearch_WithoutAToken_IsRefused()
    {
        var (tools, _) = Build(searchToken: "");

        await Should.ThrowAsync<HttpRequestException>(() => InvokeAsync(tools, "web_search", "query", "solar"));

        tools.Calls.Single().Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CodeInterpreter_EvaluatesAnExpression()
    {
        var (tools, _) = Build();

        (await InvokeAsync(tools, "code_interpreter", "code", "Math.Ceiling(18.1 / 10)")).ShouldBe("2");
        (await InvokeAsync(tools, "code_interpreter", "code", "Math.Max(3, 4) % 3 + Math.Pow(2, 3)")).ShouldBe("9");
    }

    [Fact]
    public async Task CodeInterpreter_RefusesCodeOutsideMathAndNeverRunsIt()
    {
        var (tools, _) = Build();
        AppContext.SetData("research-probe", null);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => InvokeAsync(tools, "code_interpreter", "code", "System.AppContext.SetData(\"research-probe\", 1)"));

        error.Message.ShouldContain("Code refused");
        AppContext.GetData("research-probe").ShouldBeNull();
        tools.Calls.Single().Error!.ShouldContain("Code refused");
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(path)")]
    [InlineData("Math.Sqrt(4); System.Environment.Exit(1)")]
    [InlineData("new System.Diagnostics.Process()")]
    [InlineData("Enumerable.Range(1,2000000000).Select(a => Enumerable.Range(1,2000000000).Where(b => b > 0).Count()).Sum()")]
    [InlineData("Enumerable.Range(0,2000000000).ToArray()")]
    [InlineData("\"text\".Length")]
    public void CodeGuard_RefusesAnythingButArithmetic(string code)
    {
        Should.Throw<InvalidOperationException>(() => CodeGuard.Check(code));
    }

    [Fact]
    public void CodeGuard_RefusesAnOverlongExpression()
    {
        var nested = new string('(', 5000) + "1" + new string(')', 5000);

        Should.Throw<InvalidOperationException>(() => CodeGuard.Check(nested));
        Should.Throw<InvalidOperationException>(() => CodeGuard.Check("1" + string.Concat(Enumerable.Repeat(" + 1", 100))));
    }

    [Fact]
    public async Task CodeInterpreter_HugeExponentFinishesQuickly()
    {
        var (tools, _) = Build();

        var run = InvokeAsync(tools, "code_interpreter", "code", "Math.Pow(10, 1000000000)");

        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Theory]
    [InlineData("Math.Ceiling((double)6600 / 365 / 10)")]
    [InlineData("Math.Pow(2, 10) % 7")]
    public void CodeGuard_AllowsArithmeticAndMathCalls(string code)
    {
        Should.NotThrow(() => CodeGuard.Check(code));
    }
}
