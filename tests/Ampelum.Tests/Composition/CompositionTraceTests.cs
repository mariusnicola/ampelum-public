using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

public sealed class CompositionTraceTests
{
    static TraceRecord Child(
        string moduleName,
        long? promptTokens = null,
        long? completionTokens = null,
        TimeSpan? duration = null,
        string rawResponse = "",
        Exception? error = null,
        IReadOnlyList<ChatMessage>? prompt = null)
    {
        UsageDetails? usage = (promptTokens, completionTokens) switch
        {
            (null, null) => null,
            _ => new UsageDetails
            {
                InputTokenCount = promptTokens,
                OutputTokenCount = completionTokens,
                TotalTokenCount = (promptTokens ?? 0L) + (completionTokens ?? 0L),
            },
        };
        return new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: moduleName,
            SignatureType: typeof(object),
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: prompt ?? Array.Empty<ChatMessage>(),
            RawResponse: rawResponse,
            Usage: usage,
            Duration: duration ?? TimeSpan.Zero,
            Error: error,
            Metadata: new Dictionary<string, object?>());
    }

    [Fact]
    public void Aggregate_WithEmptyChildren_Throws()
    {
        var act = () => CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            Array.Empty<TraceRecord>());
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe("childTraces");
    }

    [Fact]
    public void Aggregate_PreservesIdAndModuleName()
    {
        var id = Guid.NewGuid();
        var t = CompositionTrace.Aggregate(
            id, "Loop", typeof(string),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[] { Child("Predict") });
        t.Id.ShouldBe(id);
        t.ModuleName.ShouldBe("Loop");
        t.SignatureType.ShouldBe(typeof(string));
    }

    [Fact]
    public void Aggregate_SumsUsageAcrossChildren()
    {
        var children = new[]
        {
            Child("a", promptTokens: 10L, completionTokens: 5L),
            Child("b", promptTokens: 20L, completionTokens: 7L),
            Child("c"), // null usage â€” must be ignored, not blow up
        };
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            children);
        t.Usage.ShouldNotBeNull();
        t.Usage!.InputTokenCount.ShouldBe(30L);
        t.Usage.OutputTokenCount.ShouldBe(12L);
        t.Usage.TotalTokenCount.ShouldBe(42L);
    }

    [Fact]
    public void Aggregate_SumsDuration()
    {
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[]
            {
                Child("a", duration: TimeSpan.FromMilliseconds(10)),
                Child("b", duration: TimeSpan.FromMilliseconds(25)),
            });
        t.Duration.ShouldBe(TimeSpan.FromMilliseconds(35));
    }

    [Fact]
    public void Aggregate_ConcatenatesPromptMessagesInOrder()
    {
        var m1 = new ChatMessage(ChatRole.System, "system-from-a");
        var m2 = new ChatMessage(ChatRole.User, "user-from-a");
        var m3 = new ChatMessage(ChatRole.User, "user-from-b");
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[]
            {
                Child("a", prompt: new[] { m1, m2 }),
                Child("b", prompt: new[] { m3 }),
            });
        t.PromptMessages.Count.ShouldBe(3);
        t.PromptMessages[0].ShouldBeSameAs(m1);
        t.PromptMessages[1].ShouldBeSameAs(m2);
        t.PromptMessages[2].ShouldBeSameAs(m3);
    }

    [Fact]
    public void Aggregate_JoinsRawResponseWithPipeSeparator_SkippingEmptyEntries()
    {
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[]
            {
                Child("a", rawResponse: "first"),
                Child("b", rawResponse: ""),       // skipped
                Child("c", rawResponse: "third"),
            });
        t.RawResponse.ShouldBe("first | third");
    }

    [Fact]
    public void Aggregate_TakesFirstNonNullErrorAsAggregateError()
    {
        var firstEx = new InvalidOperationException("first");
        var laterEx = new InvalidOperationException("later");
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[]
            {
                Child("a"),                            // no error
                Child("b", error: firstEx),
                Child("c", error: laterEx),            // shadowed
            });
        t.Error.ShouldBeSameAs(firstEx);
    }

    [Fact]
    public void Aggregate_NoErrorChildren_LeavesErrorNull()
    {
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[] { Child("a"), Child("b") });
        t.Error.ShouldBeNull();
    }

    [Fact]
    public void Aggregate_PopulatesChildCountAndChildTraceIdsByDefault()
    {
        var c1 = Child("a");
        var c2 = Child("b");
        var c3 = Child("c");
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[] { c1, c2, c3 });
        t.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildCount].ShouldBe(3);
        var ids = t.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildTraceIds].ShouldBeAssignableTo<IReadOnlyList<Guid>>()!;
        ids.Count.ShouldBe(3);
        ids[0].ShouldBe(c1.Id);
        ids[1].ShouldBe(c2.Id);
        ids[2].ShouldBe(c3.Id);
    }

    [Fact]
    public void Aggregate_ExtraMetadataWinsOnKeyCollision()
    {
        var c1 = Child("a");
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "Loop", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[] { c1 },
            extraMetadata: new Dictionary<string, object?>
            {
                [SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildCount] = 99,
                [SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive] = "Loop",
            });
        t.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildCount].ShouldBe(99);
        t.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive].ShouldBe("Loop");
    }

    [Fact]
    public void Aggregate_ExtraMetadataNull_LeavesDefaultsIntact()
    {
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new[] { Child("a"), Child("b") },
            extraMetadata: null);
        t.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildCount].ShouldBe(2);
        t.Metadata.ContainsKey(SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive).ShouldBeFalse();
    }

    [Fact]
    public void Aggregate_PreservesCallerSuppliedInputsAndOutputsByReference()
    {
        var inputs = new Dictionary<string, object?> { ["q"] = "hello" };
        var outputs = new Dictionary<string, object?> { ["a"] = "world" };
        var t = CompositionTrace.Aggregate(
            Guid.NewGuid(), "X", typeof(object), inputs, outputs,
            new[] { Child("a") });
        t.Inputs.ShouldBeSameAs(inputs);
        t.Outputs.ShouldBeSameAs(outputs);
    }
}
