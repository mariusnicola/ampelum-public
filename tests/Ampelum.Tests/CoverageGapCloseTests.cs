// CoverageGapCloseTests.cs â€” v0.3 Task 26: close all remaining coverage gaps.
// Targets: BestOfN bare-throw, Parallel bool-fallback, FakeChatClient error paths,
//          SignatureAssertions (all paths), async state-machine suspension branches,
//          activity?.SetTag non-null branches, null-coalescing branches.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Composition;
using TraceKeys = SignalGrid.Ampelum.TraceKeys;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Shared fixtures & helpers
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

file sealed record QASig : ISignature
{
    [Input]  public string Question { get; init; } = "";
    [Output] public string Answer   { get; init; } = "";
}

/// <summary>Signature with two outputs â€” exercises the EnqueueStreamingOutputs nextIdx loop.</summary>
file sealed record TwoOut : ISignature
{
    [Input]  public string Q       { get; init; } = "";
    [Output] public string First   { get; init; } = "";
    [Output] public string Second  { get; init; } = "";
}

/// <summary>Signature with a bool output â€” hits the else/fallback branch in Parallel aggregation.</summary>
file sealed record BoolOut : ISignature
{
    [Input]  public string Q      { get; init; } = "";
    [Output] public bool   Flag   { get; init; }
}

/// <summary>Signature with a nullable string output â€” hit null-v branch in Parallel string join.</summary>
file sealed record NullableStrOut : ISignature
{
    [Input]  public string Q      { get; init; } = "";
    [Output] public string? Answer { get; init; }
}

/// <summary>Signature with a float output â€” hits the float type-chain branch in Parallel numerics.</summary>
file sealed record FloatOut : ISignature
{
    [Input]  public string Q     { get; init; } = "";
    [Output] public float  Score { get; init; }
}

/// <summary>Signature with a nullable float output â€” allows null values for count==0 branch in Parallel.</summary>
file sealed record NullableFloatOut : ISignature
{
    [Input]  public string Q     { get; init; } = "";
    [Output] public float? Score { get; init; }
}

/// <summary>Signature with a long output â€” hits the long type-chain branch in Parallel numerics.</summary>
file sealed record LongOut : ISignature
{
    [Input]  public string Q   { get; init; } = "";
    [Output] public long   Num { get; init; }
}

/// <summary>Signature with a double output â€” hits the double (else/mean) branch in Parallel numerics.</summary>
file sealed record DoubleOut : ISignature
{
    [Input]  public string Q     { get; init; } = "";
    [Output] public double Score { get; init; }
}

/// <summary>
/// A truly-async <see cref="IChatClient"/> that yields before returning each response,
/// forcing the async state machine to suspend.
/// </summary>
file sealed class TrulyAsyncFakeChatClient : IChatClient
{
    readonly ConcurrentQueue<object> _queue = new();
    public ChatClientMetadata Metadata { get; } = new(nameof(TrulyAsyncFakeChatClient));

    public TrulyAsyncFakeChatClient Enqueue(string text)   { _queue.Enqueue(text); return this; }
    public TrulyAsyncFakeChatClient EnqueueErr(Exception e) { _queue.Enqueue(e);   return this; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (!_queue.TryDequeue(out var item))
            throw new InvalidOperationException("TrulyAsync: empty queue");
        if (item is Exception ex) throw ex;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, (string)item));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Adapter that is not ITestableAdapter â€” used to exercise guard branches.</summary>
file sealed class PlainAdapter : IAdapter
{
    public IReadOnlyList<ChatMessage> Format(
        SignatureMetadata sig, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<Example> demos)
        => Array.Empty<ChatMessage>();
    public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata sig, ChatResponse response)
        => new Dictionary<string, object?>();
}

/// <summary>Object whose ToString() returns null â€” exercises the <c>v.ToString() ?? ""</c> branch in XMLAdapter.</summary>
file sealed class NullToStringObject
{
    public override string? ToString() => null;
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// SignatureAssertions â€” 0% in SignalGrid.Ampelum.Tests gate (pre-existing gap)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class SignatureAssertionsTests
{
    [Fact]
    public void ShouldBeValidSignature_ValidSig_DoesNotThrow()
    {
        // Exercises the happy path â€” SignatureMetadataCache.For<QASig>() succeeds.
        SignatureAssertions.ShouldBeValidSignature<QASig>();
    }

    [Fact]
    public void ShouldHaveInput_ExistingField_DoesNotThrow()
    {
        SignatureAssertions.ShouldHaveInput<QASig>("Question");
    }

    [Fact]
    public void ShouldHaveInput_MissingField_ThrowsSignatureAssertionException()
    {
        var ex = Should.Throw<SignatureAssertionException>(() =>
            SignatureAssertions.ShouldHaveInput<QASig>("NonExistent"));
        ex.Message.ShouldContain("NonExistent");
        ex.Message.ShouldContain("QASig");
    }

    [Fact]
    public void ShouldHaveOutput_ExistingField_DoesNotThrow()
    {
        SignatureAssertions.ShouldHaveOutput<QASig>("Answer");
    }

    [Fact]
    public void ShouldHaveOutput_MissingField_ThrowsSignatureAssertionException()
    {
        var ex = Should.Throw<SignatureAssertionException>(() =>
            SignatureAssertions.ShouldHaveOutput<QASig>("NoSuchOutput"));
        ex.Message.ShouldContain("NoSuchOutput");
        ex.Message.ShouldContain("QASig");
    }

    [Fact]
    public void SignatureAssertionException_StoresMessage()
    {
        // Exercises the primary constructor line (the only line with 0% hits).
        var exc = new SignatureAssertionException("test message");
        exc.Message.ShouldBe("test message");
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// FakeChatClient â€” uncovered lines: 68, 90, 123, 140-141, 159, 169, 172
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class FakeChatClientCoverageTests
{
    // Line 68 â€” EnqueueOutputs with non-ITestableAdapter
    [Fact]
    public void EnqueueOutputs_NonTestableAdapter_Throws()
    {
        var client = new FakeChatClient();
        var plain = new PlainAdapter();
        var ex = Should.Throw<InvalidOperationException>(() =>
            client.EnqueueOutputs(new QASig { Question = "q", Answer = "a" }, plain));
        ex.Message.ShouldContain("ITestableAdapter");
    }

    // Line 90 â€” GetResponseAsync with empty queue
    [Fact]
    public async Task GetResponseAsync_EmptyQueue_Throws()
    {
        var client = new FakeChatClient();
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.GetResponseAsync(Array.Empty<ChatMessage>()));
        ex.Message.ShouldContain("no enqueued response");
    }

    // Line 123 â€” EnqueueStreamingOutputs with non-ITestableAdapter
    [Fact]
    public void EnqueueStreamingOutputs_NonTestableAdapter_Throws()
    {
        var client = new FakeChatClient();
        var plain = new PlainAdapter();
        var ex = Should.Throw<InvalidOperationException>(() =>
            client.EnqueueStreamingOutputs(new QASig { Question = "q", Answer = "a" }, plain));
        ex.Message.ShouldContain("ITestableAdapter");
    }

    // Lines 140-141 â€” EnqueueStreamingOutputs multi-output nextIdx search
    [Fact]
    public async Task EnqueueStreamingOutputs_MultiOutput_FindsNextMarker()
    {
        // TwoOut has 2 outputs â€” exercises the inner loop that finds the next field marker.
        var client = new FakeChatClient().EnqueueStreamingOutputs(
            new TwoOut { Q = "?", First = "hello", Second = "world" });

        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(Array.Empty<ChatMessage>(), cancellationToken: TestContext.Current.CancellationToken))
            chunks.Add(update.Text ?? "");

        // Both field sections should be present in the combined output.
        var combined = string.Concat(chunks);
        combined.ShouldContain("First");
        combined.ShouldContain("Second");
        combined.ShouldContain("hello");
        combined.ShouldContain("world");
    }

    // Line 159 â€” GetStreamingResponseAsync with empty streaming queue
    [Fact]
    public async Task GetStreamingResponseAsync_EmptyQueue_Throws()
    {
        var client = new FakeChatClient();
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Array.Empty<ChatMessage>()))
            { }
        });
        ex.Message.ShouldContain("no enqueued streaming chunks");
    }

    // Line 169 â€” GetService
    [Fact]
    public void GetService_ReturnsNull()
    {
        var client = new FakeChatClient();
        client.GetService(typeof(string)).ShouldBeNull();
        client.GetService(typeof(string), "key").ShouldBeNull();
    }

    // Line 172 â€” Dispose
    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new FakeChatClient();
        client.Dispose(); // Must not throw.
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// BestOfN â€” line 71: bare `throw;` when no task is faulted
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

[Collection(ActivityListenerCollection.Name)]
public sealed class BestOfNCoverageTests
{
    static AmpelumContext Ctx(IChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    // When all tasks are CANCELLED (not faulted), firstFaulted is null â†’ bare throw; path.
    [Fact]
    public async Task AllTasksCancelled_BareThrowPath()
    {
        using var cts = new CancellationTokenSource();
        // Use a client that will throw OperationCanceledException when CT is cancelled.
        var client = new CancellingChatClient();
        var ctx = Ctx(client);
        var metric = new UserDefined<QASig>((_, _) => 1.0);
        var bon = new BestOfN<QASig>(ctx, metric, n: 2);

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            () => bon.ForwardAsync(new QASig { Question = "?" }, ct: cts.Token));
    }

    // Activity listener test â€” ensures activity?.SetTag lines are hit with non-null activity.
    [Fact]
    public async Task ForwardAsync_WithActivityListener_CoversActivityNonNullBranches()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nalpha")
            .EnqueueText("[[ ## Answer ## ]]\nbeta");
        var ctx = Ctx(chat);
        var metric = new UserDefined<QASig>((a, _) => a.Answer == "alpha" ? 1.0 : 0.5);
        var bon = new BestOfN<QASig>(ctx, metric, n: 2);
        var pred = await bon.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("alpha");
    }

    /// <summary>Chat client that honours cancellation by throwing OperationCanceledException.</summary>
    sealed class CancellingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("cancelling");
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Parallel â€” line 80: else branch (bool/non-numeric non-string fallback)
//            line 65: v?.ToString() ?? "" with null v
//            line 71: count == 0 path
//            line 72: float type-chain branch
//            lines 51-52: activity?.SetTag non-null branches
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

[Collection(ActivityListenerCollection.Name)]
public sealed class ParallelCoverageTests
{
    static AmpelumContext Ctx(IChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task BoolOutput_FallsBackToFirstModule()
    {
        // bool is not string and not in IsNumeric â†’ hits the else branch at line 80.
        // ChatAdapter.ConvertTo uses JSON deserialiser for bool â†’ must use lowercase true/false.
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Flag ## ]]\ntrue")
            .EnqueueText("[[ ## Flag ## ]]\nfalse");
        var mod1 = new Predict<BoolOut>(Ctx(chat));
        var mod2 = new Predict<BoolOut>(Ctx(chat));
        var par = new Parallel<BoolOut>(Ctx(chat), new[] { mod1, mod2 });
        var pred = await par.ForwardAsync(new BoolOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        // Result is the first module's value (bool fallback â†’ first module)
        pred.Value.Flag.ShouldBeTrue();
    }

    // Activity listener test for Parallel â€” covers activity?.SetTag non-null branches
    [Fact]
    public async Task ForwardAsync_WithActivityListener_CoversActivityNonNullBranches()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## Answer ## ]]\nlondon");
        var ctx = Ctx(chat);
        var mods = new[] { new Predict<QASig>(ctx), new Predict<QASig>(ctx) };
        var par = new Parallel<QASig>(ctx, mods);
        var pred = await par.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris | london");
    }

    // Line 65: v?.ToString() ?? "" with a null value â€” hits the v==null branch (v?.ToString() returns null â†’ ?? "")
    [Fact]
    public async Task StringOutput_NullValue_JoinsAsEmpty()
    {
        // Use an adapter that returns null for the Answer field of the second module.
        // XMLAdapter in Lenient mode returns null for missing fields.
        var chat1 = new FakeChatClient().EnqueueText("<Outputs><Answer>hello</Answer></Outputs>");
        var chat2 = new FakeChatClient().EnqueueText("<Outputs></Outputs>"); // no Answer â†’ null in lenient mode
        var xmlAdapter = new XMLAdapter(XMLAdapter.ParseMode.Lenient);
        var ctx1 = new AmpelumContext(chat1, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var ctx2 = new AmpelumContext(chat2, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var ctxPar = new AmpelumContext(chat1, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<NullableStrOut>(ctx1), new Predict<NullableStrOut>(ctx2) };
        var par = new Parallel<NullableStrOut>(ctxPar, mods);
        var pred = await par.ForwardAsync(new NullableStrOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        // First module produced "hello", second produced null â†’ join is "hello | "
        pred.Value.Answer.ShouldBe("hello | ");
    }

    // Line 72: float type-chain branch â€” IsNumeric(float)==true, and f.ClrType == typeof(float) branch
    [Fact]
    public async Task FloatOutput_AveragesCorrectly()
    {
        // Both modules produce float Score = 1.0 â†’ average is 1.0 cast to float.
        var chat = new FakeChatClient()
            .EnqueueText("<Outputs><Score>1</Score></Outputs>")
            .EnqueueText("<Outputs><Score>1</Score></Outputs>");
        var xmlAdapter = new XMLAdapter(XMLAdapter.ParseMode.Lenient);
        var ctx = new AmpelumContext(chat, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<FloatOut>(ctx), new Predict<FloatOut>(ctx) };
        var par = new Parallel<FloatOut>(ctx, mods);
        var pred = await par.ForwardAsync(new FloatOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Score.ShouldBe(1.0f, tolerance: 0.001f);
    }

    // Line 72: long type-chain branch
    [Fact]
    public async Task LongOutput_AveragesCorrectly()
    {
        var chat = new FakeChatClient()
            .EnqueueText("<Outputs><Num>4</Num></Outputs>")
            .EnqueueText("<Outputs><Num>6</Num></Outputs>");
        var xmlAdapter = new XMLAdapter(XMLAdapter.ParseMode.Lenient);
        var ctx = new AmpelumContext(chat, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<LongOut>(ctx), new Predict<LongOut>(ctx) };
        var par = new Parallel<LongOut>(ctx, mods);
        var pred = await par.ForwardAsync(new LongOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Num.ShouldBe(5L);
    }

    // Line 72: double type-chain branch (falls through to the else/mean path)
    [Fact]
    public async Task DoubleOutput_AveragesCorrectly()
    {
        var chat = new FakeChatClient()
            .EnqueueText("<Outputs><Score>2</Score></Outputs>")
            .EnqueueText("<Outputs><Score>4</Score></Outputs>");
        var xmlAdapter = new XMLAdapter(XMLAdapter.ParseMode.Lenient);
        var ctx = new AmpelumContext(chat, xmlAdapter, NullTraceSink.Instance, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<DoubleOut>(ctx), new Predict<DoubleOut>(ctx) };
        var par = new Parallel<DoubleOut>(ctx, mods);
        var pred = await par.ForwardAsync(new DoubleOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Score.ShouldBe(3.0);
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Refine â€” activity?.SetTag non-null branches (L54-56)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

[Collection(ActivityListenerCollection.Name)]
public sealed class RefineCoverageTests
{
    static AmpelumContext Ctx(IChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task ForwardAsync_WithActivityListener_CoversActivityNonNullBranches()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nbad")
            .EnqueueText("[[ ## Answer ## ]]\ngood");
        var ctx = Ctx(chat);
        var metric = new UserDefined<QASig>((a, _) => a.Answer == "good" ? 1.0 : 0.0);
        var refine = new Refine<QASig>(ctx, metric, threshold: 0.5, maxRefinements: 1);
        var pred = await refine.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("good");
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Evaluate â€” activity?.SetTag non-null branches (L131-132)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class EvaluateCoverageTests
{
    [Fact]
    public async Task RunAsync_WithActivityListener_CoversActivityNonNullBranches()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        static Task<Prediction<QASig>> Program(QASig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<QASig>(new QASig { Answer = "x" },
                new TraceRecord(Guid.NewGuid(), "Predict", typeof(QASig),
                    new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                    Array.Empty<ChatMessage>(), "x", null, TimeSpan.Zero, null,
                    new Dictionary<string, object?>())));

        var eval = new Evaluate<QASig>(Program, new ExactMatch<QASig>("Answer"));
        var r = await eval.RunAsync(
            new[] { new LabeledExample<QASig>(new QASig { Question = "q" }, new QASig { Answer = "x" }) },
            TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(1.0);
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// RetryModule â€” activity?.SetTag("retry.reason", "suggestion") branch (L72)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

[Collection(ActivityListenerCollection.Name)]
public sealed class RetryModuleCoverageTests
{
    [Fact]
    public async Task ForwardAsync_SuggestionException_WithActivityListener_CoversActivitySetTagBranch()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        int count = 0;
        var module = new SignalGrid.Ampelum.Retry.RetryModule<QASig>(
            (_, _) =>
            {
                count++;
                if (count == 1) throw new SignalGrid.Ampelum.Exceptions.SuggestionException("try harder");
                return Task.FromResult(new QASig { Answer = "ok" });
            },
            maxAttempts: 3);

        var result = await module.ForwardAsync(ct: TestContext.Current.CancellationToken);
        count.ShouldBe(2);
        result.Answer.ShouldBe("ok");
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Predict.ForwardAsync L136 â€” AdditionalProperties has key but value is not `true`
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class PredictCoverageTests
{
    // Covers the `hit is true` â†’ FALSE branch at Predict.cs L138:
    // When AdditionalProperties has the "SignalGrid.Ampelum.cache_hit" key but the value is NOT `true`.
    [Fact]
    public async Task ForwardAsync_CacheHitKeyFalse_DoesNotSetMetadata()
    {
        // Return a ChatResponse with AdditionalProperties["SignalGrid.Ampelum.cache_hit"] = false
        // â†’ TryGetValue succeeds, but `hit is true` fails.
        var client = new ResponseWithCacheHitFalseClient();
        var ctx = new AmpelumContext(client, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var pred = await new Predict<QASig>(ctx)
            .ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    // Covers the `TryGetValue` â†’ FALSE branch: AdditionalProperties is non-null but key absent.
    [Fact]
    public async Task ForwardAsync_NonNullAdditionalProps_NoKey_DoesNotSetMetadata()
    {
        var client = new ResponseWithEmptyAdditionalPropsClient();
        var ctx = new AmpelumContext(client, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var pred = await new Predict<QASig>(ctx)
            .ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    sealed class ResponseWithCacheHitFalseClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("cache-hit-false");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "[[ ## Answer ## ]]\ntest"));
            resp.AdditionalProperties = new Microsoft.Extensions.AI.AdditionalPropertiesDictionary
            {
                ["SignalGrid.Ampelum.cache_hit"] = false
            };
            return Task.FromResult(resp);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class ResponseWithEmptyAdditionalPropsClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("empty-additional-props");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "[[ ## Answer ## ]]\ntest"));
            // Non-null AdditionalProperties but without the "SignalGrid.Ampelum.cache_hit" key.
            resp.AdditionalProperties = new Microsoft.Extensions.AI.AdditionalPropertiesDictionary
            {
                ["other.key"] = "value"
            };
            return Task.FromResult(resp);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// F1 metric â€” null value branches (L21-22): prop.GetValue returns null â†’ as string â†’ ?? ""
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class F1CoverageTests
{
    [Fact]
    public void Score_NullActualPropertyValue_TreatedAsEmpty()
    {
        // When prop.GetValue(actual) returns null, `as string` yields null, and `?? ""` kicks in (L21).
        // This requires a nullable reference-type property with a null value.
        var f1 = new F1<QASig>("Answer");
        var meta = SignatureMetadataCache.For<QASig>();
        // Use null! to force the property to be null at runtime.
        var actualWithNull = new QASig { Answer = null! };
        var expected = new QASig { Answer = "hello world" };
        // actual has empty tokens (nullâ†’"" â†’ no tokens) and expected has tokens â†’ F1 = 0.
        var score = f1.Score(actualWithNull, expected, meta);
        score.ShouldBe(0.0);
    }

    [Fact]
    public void Score_NullExpectedPropertyValue_TreatedAsEmpty()
    {
        // When prop.GetValue(expected) returns null, `as string` yields null, and `?? ""` kicks in (L22).
        var f1 = new F1<QASig>("Answer");
        var meta = SignatureMetadataCache.For<QASig>();
        var actual = new QASig { Answer = "hello world" };
        var expectedWithNull = new QASig { Answer = null! };
        // expected has empty tokens (nullâ†’"" â†’ no tokens) â†’ F1 = 0.
        var score = f1.Score(actual, expectedWithNull, meta);
        score.ShouldBe(0.0);
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// AmpelumContext.InspectHistory â€” null inner token count branches (L81-82)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class AmpelumContextCoverageTests
{
    [Fact]
    public void InspectHistory_UsageNonNullButNullInnerTokenCounts_EmitsEmptyTokenStrings()
    {
        // Covers: r.Usage?.InputTokenCount?.ToString() ?? ""
        // when Usage is non-null but InputTokenCount / OutputTokenCount are null.
        var sink = new InMemoryTraceSink();
        var usage = new UsageDetails { InputTokenCount = null, OutputTokenCount = null };
        sink.Write(new TraceRecord(
            Guid.NewGuid(), "Predict", typeof(QASig),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            new[] { new ChatMessage(ChatRole.User, "ask") },
            "raw", usage, TimeSpan.FromMilliseconds(10), null,
            new Dictionary<string, object?>()));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var s = ctx.InspectHistory(1);
        // Both token counts are empty string because inner values are null.
        s.ShouldContain("prompt_tokens=");
        s.ShouldContain("completion_tokens=");
        // Should NOT contain any numeric token values.
        s.ShouldNotContain("prompt_tokens=1");
        s.ShouldNotContain("completion_tokens=1");
    }

    [Fact]
    public void InspectHistory_UsageNull_EmitsEmptyTokenCounts()
    {
        // Covers the null-usage branch: r.Usage?.InputTokenCount?.ToString() ?? ""
        // When Usage is null â†’ p and c are "" (both branches of ?. chain).
        var sink = new InMemoryTraceSink();
        sink.Write(new TraceRecord(
            Guid.NewGuid(), "Predict", typeof(QASig),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            new[] { new ChatMessage(ChatRole.User, "ask") },
            "raw", /* usage: */ null, TimeSpan.FromMilliseconds(10), null,
            new Dictionary<string, object?>()));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var s = ctx.InspectHistory(1);
        s.ShouldContain("prompt_tokens=");
        s.ShouldContain("completion_tokens=");
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// DefaultUsageTracker/ScopeState â€” null inner token count branches (L88-89)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class UsageTrackerCoverageTests
{
    [Fact]
    public void ScopeState_CacheHitFalse_InScope_DoesNotIncrementHits()
    {
        // Exercises the `if (hit)` false branch inside ScopeState.Record.
        var tracker = new SignalGrid.Ampelum.Usage.DefaultUsageTracker();
        using (tracker.Scope("s"))
            tracker.Record(new UsageDetails { InputTokenCount = 2 }, cacheHit: false);
        tracker.Snapshot().PerScope!["s"].CacheHits.ShouldBe(0);
    }

    [Fact]
    public void ScopeState_NullInnerTokenCounts_DoesNotAddTokens()
    {
        // Exercises the false branch of `u?.InputTokenCount is long ti` when
        // u is non-null but InputTokenCount / OutputTokenCount are null.
        var tracker = new SignalGrid.Ampelum.Usage.DefaultUsageTracker();
        using (tracker.Scope("s2"))
        {
            // Pass non-null UsageDetails with null token counts.
            tracker.Record(new UsageDetails { InputTokenCount = null, OutputTokenCount = null }, cacheHit: false);
        }
        var summary = tracker.Snapshot().PerScope!["s2"];
        summary.TotalTokens.ShouldBe(0L);
        summary.PromptTokens.ShouldBe(0L);
        summary.CompletionTokens.ShouldBe(0L);
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// XMLAdapter â€” null response.Text branch (L68) and null v.ToString() branch (L115)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class XMLAdapterCoverageTests
{
    // L115: v.ToString() returns null â†’ SecurityElement.Escape("") is called instead
    [Fact]
    public void Format_NullToStringValue_EmitsEmptyElement()
    {
        // NullToStringObject.ToString() returns null â†’ v.ToString() ?? "" â†’ "" â†’ Escape("") â†’ ""
        // We can only pass object? values via the inputs dict (not the record's typed properties).
        // Use a custom IAdapter-compatible format call with a manually constructed inputs dict.
        var adapter = new XMLAdapter();
        var meta = SignatureMetadataCache.For<QASig>();
        // Passing NullToStringObject as the value for "Question" exercises the v.ToString() ?? "" branch.
        var msgs = adapter.Format(meta,
            new Dictionary<string, object?> { ["Question"] = new NullToStringObject() },
            Array.Empty<Example>());
        // The Question element should exist but be empty (null.ToString() â†’ null ?? "" â†’ "").
        msgs.Last().Text.ShouldContain("<Question></Question>");
    }

    // L68: response.Text is null â†’ text = ""
    [Fact]
    public void Parse_NullResponseText_TreatedAsEmpty_Throws()
    {
        // Exercises the `response.Text ?? ""` null-coalescing branch (Path 0).
        // A response with no messages returns null for Text; the adapter should still throw
        // AdapterParseException (no <Outputs> block found in empty string).
        var response = new ChatResponse(messages: new List<ChatMessage>());
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            new XMLAdapter().Parse(SignatureMetadataCache.For<QASig>(), response));
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// TwoStepAdapter â€” null response.Text branch (L43) and null extractResponse.Text branch (L56)
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

public sealed class TwoStepAdapterCoverageTests
{
    // L43: response.Text is null â†’ rawText = ""
    // The extraction client receives an empty rawText and returns valid JSON.
    [Fact]
    public void Parse_NullResponseText_UsesEmptyRawText()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Answer\": \"result\"}");
        // Response with null text (no messages â†’ Text is null).
        var response = new ChatResponse(messages: new List<ChatMessage>());
        var result = new TwoStepAdapter(extract).Parse(
            SignatureMetadataCache.For<QASig>(), response);
        result["Answer"].ShouldBe("result");
    }

    // L56: extractResponse.Text is null â†’ jsonText = "" â†’ throws AdapterParseException (invalid JSON)
    [Fact]
    public void Parse_NullExtractionResponseText_ThrowsAdapterParseException()
    {
        // Return an extraction response that has null Text (no messages).
        var extract = new NullTextChatClient();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw text"));
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            new TwoStepAdapter(extract).Parse(SignatureMetadataCache.For<QASig>(), response));
    }

    /// <summary>Chat client that always returns a ChatResponse with null Text (no messages).</summary>
    sealed class NullTextChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("null-text");
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(messages: new List<ChatMessage>()));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// Async state-machine suspension branches
//
// Each await point in an async method generates two compiler branches:
//   â€¢ the "already complete" path (fast path, no suspension)
//   â€¢ the "suspension" path (awaited task hadn't finished yet)
// The tests below use TrulyAsyncFakeChatClient (which does Task.Yield())
// so the awaited task is NOT complete at the await site, triggering the
// suspension branch for each state machine.
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

[Collection(ActivityListenerCollection.Name)]
public sealed class AsyncSuspensionBranchTests
{
    // â”€â”€ BestOfN async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task BestOfN_TrulyAsync_CoversSuspensionBranch()
    {
        var chat = new TrulyAsyncFakeChatClient()
            .Enqueue("[[ ## Answer ## ]]\nalpha")
            .Enqueue("[[ ## Answer ## ]]\nbeta");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var metric = new UserDefined<QASig>((a, _) => a.Answer == "alpha" ? 1.0 : 0.5);
        var bon = new BestOfN<QASig>(ctx, metric, n: 2);
        var pred = await bon.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("alpha");
    }

    // â”€â”€ Parallel async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Parallel_TrulyAsync_CoversSuspensionBranch()
    {
        var chat = new TrulyAsyncFakeChatClient()
            .Enqueue("[[ ## Answer ## ]]\nparis")
            .Enqueue("[[ ## Answer ## ]]\nlondon");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<QASig>(ctx), new Predict<QASig>(ctx) };
        var par = new Parallel<QASig>(ctx, mods);
        var pred = await par.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        // Both calls race for the shared reply queue, so either order is valid.
        pred.Value.Answer.ShouldBeOneOf("paris | london", "london | paris");
    }

    // â”€â”€ MultiChainComparison.RunAggregatorCall async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task MultiChainComparison_MajorityVote_TrulyAsync_CoversSuspensionBranch()
    {
        // MajorityVote path calls RunAggregatorCall which has a single await.
        var chat = new TrulyAsyncFakeChatClient()
            .Enqueue("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .Enqueue("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .Enqueue("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis"); // aggregation call
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var m = new MultiChainComparison<QASig>(ctx, n: 2, AggregatorStrategy.MajorityVote);
        var pred = await m.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
    }

    // â”€â”€ MultiChainComparison.RunModelAggregator async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task MultiChainComparison_ModelSynthesize_TrulyAsync_CoversSuspensionBranch()
    {
        var chat = new TrulyAsyncFakeChatClient()
            .Enqueue("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .Enqueue("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .Enqueue("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var m = new MultiChainComparison<QASig>(ctx, n: 2, AggregatorStrategy.ModelSynthesize);
        var pred = await m.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
    }

    // â”€â”€ Refine async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Refine_TrulyAsync_CoversSuspensionBranch()
    {
        // maxRefinements=1 â†’ two LM calls. Both use TrulyAsync to suspend.
        var chat = new TrulyAsyncFakeChatClient()
            .Enqueue("[[ ## Answer ## ]]\nbad")
            .Enqueue("[[ ## Answer ## ]]\ngood");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var metric = new UserDefined<QASig>((a, _) => a.Answer == "good" ? 1.0 : 0.0);
        var refine = new Refine<QASig>(ctx, metric, threshold: 0.5, maxRefinements: 1);
        var pred = await refine.ForwardAsync(new QASig { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("good");
    }

    // â”€â”€ Evaluate async suspension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Evaluate_TrulyAsync_CoversSuspensionBranch()
    {
        // Use a truly-async program delegate so the await inside RunAsync suspends.
        static async Task<Prediction<QASig>> TrulyAsyncProgram(QASig inputs, CancellationToken ct)
        {
            await Task.Yield();
            return new Prediction<QASig>(new QASig { Answer = "x" },
                new TraceRecord(Guid.NewGuid(), "Predict", typeof(QASig),
                    new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                    Array.Empty<ChatMessage>(), "x", null, TimeSpan.Zero, null,
                    new Dictionary<string, object?>()));
        }

        var eval = new Evaluate<QASig>(TrulyAsyncProgram, new ExactMatch<QASig>("Answer"));
        var r = await eval.RunAsync(
            new[] { new LabeledExample<QASig>(new QASig { Question = "q" }, new QASig { Answer = "x" }) },
            TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(1.0);
    }

    // â”€â”€ RetryModule: SuggestionException path (verifying it enters `catch(SuggestionException)`) â”€

    [Fact]
    public async Task RetryModule_TrulyAsync_SuggestionException_SuspensionBranch()
    {
        int count = 0;
        var module = new SignalGrid.Ampelum.Retry.RetryModule<QASig>(
            async (_, ct) =>
            {
                await Task.Yield();
                count++;
                if (count == 1) throw new SignalGrid.Ampelum.Exceptions.SuggestionException("try harder");
                return new QASig { Answer = "ok" };
            },
            maxAttempts: 3);

        var result = await module.ForwardAsync(ct: TestContext.Current.CancellationToken);
        count.ShouldBe(2);
        result.Answer.ShouldBe("ok");
    }

    // â”€â”€ XMLAdapter: end < start branch â€” text with </Outputs> before <Outputs> â”€â”€

    [Fact]
    public void XMLAdapter_EndBeforeStart_ThrowsAdapterParseException()
    {
        // Exercises: `end < start` in the XML extraction logic.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "</Outputs> some text <Outputs>"));
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            new XMLAdapter().Parse(SignatureMetadataCache.For<QASig>(), response));
    }
}
