using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class ChatAdapterStreamingTests
{
    [Signature("")] public sealed record S : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    [Signature("")] public sealed record S2 : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
        [Output] public string B { get; init; } = "";
    }

    static SignatureMetadata Meta() => SignatureMetadataCache.For<S>();
    static SignatureMetadata Meta2() => SignatureMetadataCache.For<S2>();

    static StreamingParseState InitialState() => new(
        CurrentFieldName: "",
        AccumulatedText: "",
        PendingTail: "",
        CompletedFields: new Dictionary<string, object?>(),
        IsComplete: false);

    static ChatResponseUpdate Chunk(string text) =>
        new(ChatRole.Assistant, text);

    [Fact]
    public void OnChunk_SplitsAcrossBoundaries()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        // Split the marker across two chunks: "[[ ## A " / "## ]]\nPar" / "is"
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A "));
        s = adapter.OnChunk(meta, s, Chunk("## ]]\nPar"));
        s = adapter.OnChunk(meta, s, Chunk("is"));
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    [Fact]
    public void OnChunk_PendingTail_IsLengthCapped()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        var longish = "[[ ## A ## ]]\n" + new string('x', 500);
        s = adapter.OnChunk(meta, s, Chunk(longish));
        s.PendingTail.Length.ShouldBeLessThanOrEqualTo(StreamingParseState.MaxPartialMarkerLength);
    }

    [Fact]
    public void Finalize_MissingField_ThrowsAdapterParseException()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("garbage without markers"));
        Should.Throw<AdapterParseException>(() => adapter.Finalize(meta, s));
    }

    [Fact]
    public void Finalize_FlushesPendingTail()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ bad tail"));
        var outs = adapter.Finalize(meta, s);
        ((string)outs["A"]!).ShouldContain("hello");
    }

    [Fact]
    public void ImplementsIStreamingAdapter_Directly() =>
        (new ChatAdapter() is IStreamingAdapter).ShouldBeTrue();

    // ── Additional coverage tests ────────────────────────────────────────────

    [Fact]
    public void OnChunk_EmptyChunk_ReturnsUnchangedState()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        var s2 = adapter.OnChunk(meta, s, Chunk(""));
        s2.ShouldBe(s);
    }

    [Fact]
    public void OnChunk_NullChunkText_ReturnsUnchangedState()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        // ChatResponseUpdate with null text
        var s2 = adapter.OnChunk(meta, s, new ChatResponseUpdate(ChatRole.Assistant, (string?)null));
        s2.ShouldBe(s);
    }

    [Fact]
    public void OnChunk_ThrowsOnNullSignature()
    {
        var adapter = new ChatAdapter();
        var s = InitialState();
        Should.Throw<ArgumentNullException>(() =>
            adapter.OnChunk(null!, s, Chunk("x")));
    }

    [Fact]
    public void OnChunk_ThrowsOnNullState()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        Should.Throw<ArgumentNullException>(() =>
            adapter.OnChunk(meta, null!, Chunk("x")));
    }

    [Fact]
    public void OnChunk_ThrowsOnNullChunk()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        Should.Throw<ArgumentNullException>(() =>
            adapter.OnChunk(meta, s, null!));
    }

    [Fact]
    public void Finalize_ThrowsOnNullSignature()
    {
        var adapter = new ChatAdapter();
        var s = InitialState();
        Should.Throw<ArgumentNullException>(() =>
            adapter.Finalize(null!, s));
    }

    [Fact]
    public void Finalize_ThrowsOnNullState()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        Should.Throw<ArgumentNullException>(() =>
            adapter.Finalize(meta, null!));
    }

    [Fact]
    public void OnChunk_SingleChunkCompleteMarkerAndContent()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nParis"));
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    [Fact]
    public void OnChunk_PreMarkerJunkIsDiscarded()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("junk before\n[[ ## A ## ]]\nParis"));
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    [Fact]
    public void OnChunk_PartialMarkerBracketAtEndBecomesTail()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        // Chunk ends with "[" which could start a marker
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello["));
        s.PendingTail.ShouldBe("[");
    }

    [Fact]
    public void OnChunk_OvercappedTailIsDropped()
    {
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        // Build a state with a tail that exceeds MaxPartialMarkerLength.
        // Use a text that ends with many non-marker '[' chars to force overcap detection.
        var longTail = "[[ ## A ## ]]\n" + new string('x', 100);
        s = adapter.OnChunk(meta, s, Chunk(longTail));
        // PendingTail should not exceed MaxPartialMarkerLength.
        s.PendingTail.Length.ShouldBeLessThanOrEqualTo(StreamingParseState.MaxPartialMarkerLength);
    }

    // ── Coverage: ProcessBuffer paths ────────────────────────────────────────

    [Fact]
    public void ProcessBuffer_EmptyNameMarker_IsSkipped()
    {
        // "[[ ##  ## ]]" has an empty trimmed name — should be skipped; real A marker must still work.
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ##  ## ]]\n[[ ## A ## ]]\nParis"));
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    [Fact]
    public void ProcessBuffer_TextBetweenTwoMarkers_AccumulatesInFirstField()
    {
        // Two-output signature: A then B. Text between markers goes into A.
        var adapter = new ChatAdapter();
        var meta = Meta2();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello world[[ ## B ## ]]\nfoo"));
        var outs = adapter.Finalize(meta, s);
        ((string)outs["A"]!).ShouldContain("hello world");
        outs["B"].ShouldBe("foo");
    }

    [Fact]
    public void Finalize_NullValueInCompletedFields_TreatedAsEmpty()
    {
        // Manually build a state with a null value in CompletedFields, then Finalize.
        var adapter = new ChatAdapter();
        var meta = Meta();
        var completedWithNull = new Dictionary<string, object?> { ["A"] = null };
        var s = new StreamingParseState(
            CurrentFieldName: "A",
            AccumulatedText: "",
            PendingTail: "Paris",
            CompletedFields: completedWithNull,
            IsComplete: false);
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    [Fact]
    public void ProcessBuffer_NullValueInCompletedFields_TreatedAsEmpty()
    {
        // Feed OnChunk with a state that has a null value in CompletedFields.
        var adapter = new ChatAdapter();
        var meta = Meta();
        var completedWithNull = new Dictionary<string, object?> { ["A"] = null };
        var s = new StreamingParseState(
            CurrentFieldName: "A",
            AccumulatedText: "",
            PendingTail: "",
            CompletedFields: completedWithNull,
            IsComplete: false);
        s = adapter.OnChunk(meta, s, Chunk("Paris"));
        var outs = adapter.Finalize(meta, s);
        outs["A"].ShouldBe("Paris");
    }

    // ── Coverage: IsMarkerPrefix / IsValidNameChars paths ────────────────────

    [Fact]
    public void OnChunk_PartialMarkerWithTerminatorSuffixAndEmptyName_IsTail()
    {
        // Buffer ending with "[[ ## " (6 chars of "[[ ## " + space = partial terminator, empty name).
        // This triggers the namePart.IsEmpty → true branch in IsMarkerPrefix.
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        // The tail "[[ ##  " has rest=" ", which is the first char of " ## ]]".
        // So IsMarkerPrefix("[[ ##  ") → rest=" ", term.StartsWith(" ")→true, namePart=""→IsEmpty→true.
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ ##  "));
        s.PendingTail.ShouldBe("[[ ##  ");
    }

    [Fact]
    public void OnChunk_PartialMarkerWithPureNameChars_IsTail()
    {
        // Buffer ending with "[[ ## ABC" — rest is "ABC" (no suffix of term matches), falls to IsValidNameChars.
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ ## ABC"));
        s.PendingTail.ShouldBe("[[ ## ABC");
    }

    [Fact]
    public void OnChunk_PartialMarkerWithBracketInName_FallsBackToSingleBracketTail()
    {
        // Buffer ending with "[[ ## A[" — IsValidNameChars("A[") → false (found '[').
        // The algorithm falls back: k=1 is "[" which is a valid prefix → tail = "[".
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ ## A["));
        s.PendingTail.ShouldBe("[");
    }

    [Fact]
    public void OnChunk_PartialMarkerWithClosingBracketInName_NoTail()
    {
        // Buffer ending with "[[ ## A]" — IsValidNameChars("A]") → false (found ']').
        // No shorter suffix is a valid prefix ("]" doesn't start "[[ ## ") → tail = "".
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ ## A]"));
        s.PendingTail.ShouldBe("");
    }

    [Fact]
    public void OnChunk_PartialMarkerWithHashInName_NoTail()
    {
        // Buffer ending with "[[ ## A#" — IsValidNameChars("A#") → false (found '#').
        // "#" doesn't start "[[ ## " → tail = "".
        var adapter = new ChatAdapter();
        var meta = Meta();
        var s = InitialState();
        s = adapter.OnChunk(meta, s, Chunk("[[ ## A ## ]]\nhello[[ ## A#"));
        s.PendingTail.ShouldBe("");
    }
}
