using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SignalGrid.Ampelum.Analyzers;
using Ampelum.Samples.BuildTimeTools.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.BuildTimeTools.Tests;

// Code rules: AMP013 (dynamic), AMP014 (raw LLM calls), AMP015 (ISignature needs [Signature]).
public sealed class CodeRuleTests
{
    // The same bare call is used by the firing test and by the opt-out test. The usings
    // must come first in the source, so they are kept apart from the rest.
    const string BareCallUsings = """
        using System.Threading.Tasks;
        using Microsoft.Extensions.AI;
        """;

    const string BareCallBody = """
        public static class BareCall
        {
            public static async Task<string> AskAsync(IChatClient client)
            {
                var reply = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });
                return reply.Text;
            }
        }
        """;

    // The analyzer looks up this attribute by name. The snippet declares it, because
    // no attribute of this name ships in the Ampelum assemblies.
    const string OptOutAttributeDeclaration = """
        [assembly: SignalGrid.Ampelum.Analyzers.AllowBareLlmPromptsAttribute]

        namespace SignalGrid.Ampelum.Analyzers
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class AllowBareLlmPromptsAttribute : System.Attribute { }
        }

        """;

    // Stand-in for an invocation named Predict in namespace SignalGrid.Ampelum. The
    // real Predict API has no method of that name, so the branch is reached this way.
    const string PredictStandIn = """
        namespace SignalGrid.Ampelum
        {
            public static class Stand
            {
                public static string Predict(string text) => text;
            }
        }

        public static class Caller
        {
            public static string Run() => SignalGrid.Ampelum.Stand.Predict("x");
        }
        """;

    [Fact]
    public async Task Amp013_DynamicLocal_Fires()
    {
        const string source = """
            public static class Dynamics
            {
                public static int Run()
                {
                    dynamic value = 1;
                    return value;
                }
            }
            """;

        var hit = (await Hits(new Amp013NoDynamicAnalyzer(), source, "AMP013")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("dynamic", 5, 9));
        hit.GetMessage().ShouldStartWith("'dynamic' keyword used in 'member'");
    }

    [Fact]
    public async Task Amp013_TypedLocal_IsQuiet()
    {
        const string source = """
            public static class Dynamics
            {
                public static int Run()
                {
                    object value = 1;
                    return (int)value;
                }
            }
            """;

        (await Hits(new Amp013NoDynamicAnalyzer(), source, "AMP013")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp014_BareChatCall_Fires()
    {
        var hits = await Hits(new Amp014NoBareLlmPromptsAnalyzer(), BareCallUsings + BareCallBody, "AMP014");

        hits.Length.ShouldBe(2);
        var call = hits.Single(h => AnalyzerVerifier.Where(h).Text.StartsWith("client."));
        AnalyzerVerifier.Where(call).ShouldBe(("client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, \"hi\") })", 6, 27));
        call.GetMessage().ShouldContain("Raw LLM API call 'IChatClient.GetResponseAsync'");

        var creation = hits.Single(h => AnalyzerVerifier.Where(h).Text.StartsWith("new ChatMessage"));
        AnalyzerVerifier.Where(creation).ShouldBe(("new ChatMessage(ChatRole.User, \"hi\")", 6, 59));
        creation.GetMessage().ShouldContain("Raw LLM API call 'new ChatMessage(role, content)'");
    }

    [Fact]
    public async Task Amp014_BareChatCallWithOptOutAttribute_IsQuiet()
    {
        // Same call as the firing test, with the opt-out declared on the assembly.
        var source = BareCallUsings + OptOutAttributeDeclaration + BareCallBody;

        (await Hits(new Amp014NoBareLlmPromptsAnalyzer(), source, "AMP014")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp014_PredictStandIn_Fires()
    {
        var hit = (await Hits(new Amp014NoBareLlmPromptsAnalyzer(), PredictStandIn, "AMP014")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("SignalGrid.Ampelum.Stand.Predict(\"x\")", 11, 35));
        hit.GetMessage().ShouldContain("Raw LLM API call 'Predict<>'");
    }

    [Fact]
    public async Task Amp014_PredictStandInInAgentAssembly_IsQuiet()
    {
        // The analyzer exempts assemblies whose name contains SignalGrid.Ampelum.Agent.
        (await Hits(new Amp014NoBareLlmPromptsAnalyzer(), PredictStandIn, "AMP014", "SignalGrid.Ampelum.Agent.Tests"))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp015_SignatureWithoutAttribute_Fires()
    {
        const string source = """
            using SignalGrid.Ampelum;

            public sealed record Unannotated : ISignature;
            """;

        var hit = (await Hits(new Amp015SignatureRequiredAnalyzer(), source, "AMP015")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("Unannotated", 3, 22));
        hit.GetMessage().ShouldBe("Type 'Unannotated' implements ISignature but is not annotated with [Signature]");
    }

    [Fact]
    public async Task Amp015_SignatureWithAttribute_IsQuiet()
    {
        const string source = """
            using SignalGrid.Ampelum;

            [Signature("Answer the question.")]
            public sealed record Annotated : ISignature;
            """;

        (await Hits(new Amp015SignatureRequiredAnalyzer(), source, "AMP015")).ShouldBeEmpty();
    }

    static async Task<ImmutableArray<Diagnostic>> Hits(
        DiagnosticAnalyzer analyzer, string source, string id,
        string assemblyName = AnalyzerVerifier.DefaultAssemblyName)
    {
        var all = await AnalyzerVerifier.RunAsync(
            analyzer, source, TestContext.Current.CancellationToken, globalConfig: null, assemblyName: assemblyName);
        return all.Where(d => d.Id == id).ToImmutableArray();
    }
}
