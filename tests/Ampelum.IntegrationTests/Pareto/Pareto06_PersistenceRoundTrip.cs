using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Persistence;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #6 (master plan Â§6.H, alpha.2). Persistence round-trip:
/// <see cref="JsonProgramPersistence.SaveAsync{TProgram}"/> â†’
/// <see cref="JsonProgramPersistence.LoadPredictAsync{TSig}"/> preserves demo assignments and
/// instruction overrides, and the <see cref="OptimizedProgram{TProgram}.Bind"/>'d program
/// produces an output bit-identical to a freshly bound program over the same inputs against
/// the same fake LM.
/// </summary>
/// <remarks>
/// alpha.3 promotion: the v1.0 ergonomic gap is closed â€” <see cref="JsonProgramPersistence.LoadPredictAsync{TSig}"/>
/// accepts an <see cref="AmpelumContext"/> directly so <see cref="Predict{TSig}"/>'s lack of a
/// public parameterless ctor no longer blocks load. The previous parameterless <c>QaProgram</c>
/// shim has been removed.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Pareto06_PersistenceRoundTrip : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto06_PersistenceRoundTrip(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Save â†’ LoadPredict â†’ Bind produces a program whose forward output matches the pre-save baseline.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task PersistenceRoundTrip_SaveLoadBind_ProducesIdenticalAnswer_DemosPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        const string submoduleName = nameof(Predict);

        // Baseline OptimizedProgram with explicit demos + instruction override on the single submodule.
        var demoExamples = new List<Example>
        {
            new(new Dictionary<string, object?> { ["Question"] = "Capital of France?" },
                new Dictionary<string, object?> { ["Answer"]   = "Paris" }),
            new(new Dictionary<string, object?> { ["Question"] = "Capital of Spain?" },
                new Dictionary<string, object?> { ["Answer"]   = "Madrid" }),
        };
        var demosBySubmodule = new Dictionary<string, IReadOnlyList<Example>>
        {
            [submoduleName] = demoExamples,
        };
        var overrides = new Dictionary<string, string>
        {
            [submoduleName] = "Answer with one factoid word.",
        };

        // A throwaway context just so the seed Predict<QaSig> can be constructed.
        using var seedFake = new FakeChatClient();
        var seedCtx = new AmpelumContext(
            seedFake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        var optimized = new OptimizedProgram<Predict<QaSig>>(
            Program: new Predict<QaSig>(seedCtx),
            DemosBySubmodule: demosBySubmodule,
            InstructionOverrides: overrides);

        // Round-trip through disk.
        var path = Path.Combine(Path.GetTempPath(),
            $"ampelum-pareto06-{Guid.NewGuid():N}.json");
        try
        {
            var persistence = new JsonProgramPersistence();
            await persistence.SaveAsync(optimized, path, ct);
            File.Exists(path).ShouldBeTrue();
            new FileInfo(path).Length.ShouldBeGreaterThan(0);

            // Load directly via the new ergonomic overload â€” no shim required.
            using var loadFake = new FakeChatClient();
            var loadCtx = new AmpelumContext(
                loadFake,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance);
            var restored = await persistence.LoadPredictAsync<QaSig>(path, loadCtx, ct);

            // Demos preserved.
            restored.DemosBySubmodule.ShouldContainKey(submoduleName);
            restored.DemosBySubmodule[submoduleName].Count.ShouldBe(2);
            restored.DemosBySubmodule[submoduleName][0].Inputs["Question"].ShouldBe("Capital of France?");
            restored.DemosBySubmodule[submoduleName][0].Outputs["Answer"].ShouldBe("Paris");

            // Instruction overrides preserved.
            restored.InstructionOverrides.ShouldContainKey(submoduleName);
            restored.InstructionOverrides[submoduleName].ShouldBe("Answer with one factoid word.");

            // Replay determinism: bind both originals against the same FakeChatClient script,
            // run a single forward, expect byte-identical Answer fields.
            const string responseText = "[[ ## Answer ## ]]\nLisbon\n[[ ## completed ## ]]";

            using var fakeBaseline = new FakeChatClient();
            fakeBaseline.EnqueueText(responseText);
            var ctxBaseline = new AmpelumContext(
                fakeBaseline,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance);
            var baselineBound = optimized.Bind(ctxBaseline);
            var baselineResult = await baselineBound.ForwardAsync(
                new QaSig { Question = "Capital of Portugal?" }, ct: ct);

            using var fakeRestored = new FakeChatClient();
            fakeRestored.EnqueueText(responseText);
            var ctxRestored = new AmpelumContext(
                fakeRestored,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance);
            var restoredBound = restored.Bind(ctxRestored);
            var restoredResult = await restoredBound.ForwardAsync(
                new QaSig { Question = "Capital of Portugal?" }, ct: ct);

            restoredResult.Value.Answer.ShouldBe(baselineResult.Value.Answer);
            restoredResult.Value.Answer.ShouldBe("Lisbon");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
