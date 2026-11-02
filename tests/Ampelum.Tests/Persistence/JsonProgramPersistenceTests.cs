using System.Text.Json;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Persistence;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Persistence;

public sealed class JsonProgramPersistenceTests : IDisposable
{
    readonly string _dir = Path.Combine(
        Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public JsonProgramPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task RoundTrip_DemosBySubmodule_Preserved()
    {
        var p = new JsonProgramPersistence();
        var path = P("demos.json");
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["qa"] = new List<Example>
            {
                new(
                    new Dictionary<string, object?> { ["Question"] = "2+2?" },
                    new Dictionary<string, object?> { ["Answer"] = "4" }),
            },
        };
        await p.SaveAsync(new OptimizedProgram<FakeProg>(
            new FakeProg(), demos, new Dictionary<string, string>()), path,
            TestContext.Current.CancellationToken);

        var loaded = await p.LoadAsync<FakeProg>(path,
            TestContext.Current.CancellationToken);

        loaded.DemosBySubmodule["qa"][0].Outputs["Answer"].ShouldBe("4");
        loaded.DemosBySubmodule["qa"][0].Inputs["Question"].ShouldBe("2+2?");
    }

    [Fact]
    public async Task RoundTrip_InstructionOverrides_Preserved()
    {
        var p = new JsonProgramPersistence();
        var path = P("overrides.json");
        await p.SaveAsync(new OptimizedProgram<FakeProg>(
            new FakeProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string> { ["qa"] = "Be concise." }), path,
            TestContext.Current.CancellationToken);

        var loaded = await p.LoadAsync<FakeProg>(path,
            TestContext.Current.CancellationToken);

        loaded.InstructionOverrides["qa"].ShouldBe("Be concise.");
    }

    [Fact]
    public async Task SaveAsync_WritesSnakeCaseKeys()
    {
        var p = new JsonProgramPersistence();
        var path = P("wire.json");
        await p.SaveAsync(Empty(), path, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path,
            TestContext.Current.CancellationToken));
        doc.RootElement.TryGetProperty("demo_assignments", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("instruction_overrides", out _).ShouldBeTrue();
        doc.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("tprogram_type").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SaveAsync_OverwritesAtomically()
    {
        var p = new JsonProgramPersistence();
        var path = P("atomic.json");
        await p.SaveAsync(new OptimizedProgram<FakeProg>(
            new FakeProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string> { ["a"] = "v1" }), path,
            TestContext.Current.CancellationToken);
        await p.SaveAsync(new OptimizedProgram<FakeProg>(
            new FakeProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string> { ["a"] = "v2" }), path,
            TestContext.Current.CancellationToken);

        var loaded = await p.LoadAsync<FakeProg>(path,
            TestContext.Current.CancellationToken);
        loaded.InstructionOverrides["a"].ShouldBe("v2");
    }

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsPersistenceException() =>
        await Should.ThrowAsync<ProgramPersistenceException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(P("none.json"),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task LoadAsync_DirectoryNotFound_ThrowsPersistenceException() =>
        await Should.ThrowAsync<ProgramPersistenceException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(
                Path.Combine(_dir, "no_such_subdir", "x.json"),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsPersistenceException()
    {
        var path = P("bad.json");
        await File.WriteAllTextAsync(path, "{ bad !!",
            TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ProgramPersistenceException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(path,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_NullDeserialisation_ThrowsPersistenceException()
    {
        var path = P("null.json");
        await File.WriteAllTextAsync(path, "null",
            TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ProgramPersistenceException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(path,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_WrongVersion_ThrowsPersistenceException()
    {
        var path = P("ver99.json");
        await File.WriteAllTextAsync(path,
            """{"version":99,"tprogram_type":"X","demo_assignments":{},"instruction_overrides":{}}""",
            TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ProgramPersistenceException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(path,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_NullProgram_Throws() =>
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new JsonProgramPersistence().SaveAsync<FakeProg>(null!, P("x.json"),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task SaveAsync_NullPath_Throws() =>
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new JsonProgramPersistence().SaveAsync(Empty(), null!,
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task LoadAsync_NullPath_Throws() =>
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(null!,
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task SaveAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            new JsonProgramPersistence().SaveAsync(Empty(), P("cancel.json"), cts.Token));
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_Throws()
    {
        var path = P("cancelload.json");
        await new JsonProgramPersistence().SaveAsync(Empty(), path,
            TestContext.Current.CancellationToken);
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            new JsonProgramPersistence().LoadAsync<FakeProg>(path, cts.Token));
    }

    [Fact]
    public async Task RoundTrip_NumericValues_PreservesAsNumber()
    {
        var path = P("num.json");
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["m"] = new List<Example>
            {
                new(new Dictionary<string, object?> { ["x"] = 42 },
                    new Dictionary<string, object?> { ["y"] = 3.14 }),
            },
        };
        await new JsonProgramPersistence().SaveAsync(
            new OptimizedProgram<FakeProg>(new FakeProg(), demos,
                new Dictionary<string, string>()),
            path, TestContext.Current.CancellationToken);

        var loaded = await new JsonProgramPersistence().LoadAsync<FakeProg>(
            path, TestContext.Current.CancellationToken);

        loaded.DemosBySubmodule["m"][0].Inputs["x"].ShouldBe(42L);
        loaded.DemosBySubmodule["m"][0].Outputs["y"].ShouldBe(3.14);
    }

    [Fact]
    public async Task RoundTrip_BoolAndNull_Preserved()
    {
        var path = P("misc.json");
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["m"] = new List<Example>
            {
                new(new Dictionary<string, object?> { ["t"] = true, ["f"] = false, ["n"] = (object?)null },
                    new Dictionary<string, object?>()),
            },
        };
        await new JsonProgramPersistence().SaveAsync(
            new OptimizedProgram<FakeProg>(new FakeProg(), demos,
                new Dictionary<string, string>()),
            path, TestContext.Current.CancellationToken);

        var loaded = await new JsonProgramPersistence().LoadAsync<FakeProg>(
            path, TestContext.Current.CancellationToken);

        loaded.DemosBySubmodule["m"][0].Inputs["t"].ShouldBe(true);
        loaded.DemosBySubmodule["m"][0].Inputs["f"].ShouldBe(false);
        loaded.DemosBySubmodule["m"][0].Inputs["n"].ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_ObjectValue_FallsBackToRawText()
    {
        // Exercise the JsonValueKind.Object/Array branch of UnboxJsonElement.
        var path = P("obj.json");
        var json = """
            {"version":1,"tprogram_type":"X",
             "demo_assignments":{"m":[{"inputs":{"o":{"k":1}},"outputs":{}}]},
             "instruction_overrides":{}}
            """;
        await File.WriteAllTextAsync(path, json,
            TestContext.Current.CancellationToken);

        var loaded = await new JsonProgramPersistence().LoadAsync<FakeProg>(
            path, TestContext.Current.CancellationToken);

        loaded.DemosBySubmodule["m"][0].Inputs["o"].ShouldBeOfType<string>();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    public async Task LoadPredictAsync_ReadsDemosAndOverrides_NoNewConstraint()
    {
        var ct = TestContext.Current.CancellationToken;
        var p = new JsonProgramPersistence();
        var path = P("predict.json");

        // Save via the existing typed path (FakeProg) — only the wire format matters here;
        // LoadPredictAsync ignores TProgramType.
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            [nameof(Predict)] = new List<Example>
            {
                new(
                    new Dictionary<string, object?> { ["Question"] = "ping" },
                    new Dictionary<string, object?> { ["Answer"]   = "pong" }),
            },
        };
        var overrides = new Dictionary<string, string> { [nameof(Predict)] = "Be brief." };
        await p.SaveAsync(
            new OptimizedProgram<FakeProg>(new FakeProg(), demos, overrides), path, ct);

        using var fake = new FakeChatClient();
        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);

        var loaded = await p.LoadPredictAsync<TinyQaSig>(path, ctx, ct);
        loaded.DemosBySubmodule[nameof(Predict)][0].Inputs["Question"].ShouldBe("ping");
        loaded.InstructionOverrides[nameof(Predict)].ShouldBe("Be brief.");
        loaded.Program.ShouldBeOfType<Predict<TinyQaSig>>();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    public async Task LoadPredictAsync_NullPath_Throws()
    {
        var p = new JsonProgramPersistence();
        using var fake = new FakeChatClient();
        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await p.LoadPredictAsync<TinyQaSig>(null!, ctx,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    public async Task LoadPredictAsync_NullContext_Throws()
    {
        var p = new JsonProgramPersistence();
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await p.LoadPredictAsync<TinyQaSig>(P("any.json"), null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    public async Task LoadPredictAsync_FileMissing_Throws()
    {
        var p = new JsonProgramPersistence();
        using var fake = new FakeChatClient();
        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);
        await Should.ThrowAsync<ProgramPersistenceException>(
            async () => await p.LoadPredictAsync<TinyQaSig>(
                P("nope-" + Guid.NewGuid().ToString("N") + ".json"), ctx,
                TestContext.Current.CancellationToken));
    }

    [Signature("Be brief.")]
    public sealed record TinyQaSig : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    string P(string name) => Path.Combine(_dir, name);

    static OptimizedProgram<FakeProg> Empty() =>
        new(new FakeProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());

    public sealed class FakeProg { }
}
