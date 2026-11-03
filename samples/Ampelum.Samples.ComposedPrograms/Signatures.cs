using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms;

[Signature("Classify one product change. Kind is one of Feature, Fix, Breaking, Internal. Impact is a whole number from 1 (barely noticeable) to 5 (affects every user).")]
public sealed record ClassifySig : ISignature
{
    [Input] public required string Title { get; init; }
    [Input] public required string Details { get; init; }
    [Output] public string Kind { get; init; } = "";
    [Output] public int Impact { get; init; }
}

[Signature("Write one release note line for the change. Use the tone and the style notes you are given.")]
public sealed record DraftSig : ISignature
{
    [Input] public required string Title { get; init; }
    [Input] public required string Details { get; init; }
    [Input] public string Kind { get; init; } = "";
    [Input] public int Impact { get; init; }
    [Input] public string StyleNotes { get; init; } = "";
    // Set by WithConfig from the options, so it needs a setter.
    [Input] public string Tone { get; set; } = "";
    [Output] public string Note { get; init; } = "";
}

[Signature("Shorten the release note without losing the change a user can see.")]
public sealed record TightenSig : ISignature
{
    [Input] public required string Title { get; init; }
    [Input] public required string Draft { get; init; }
    [Input] public string Kind { get; init; } = "";
    [Input] public int Impact { get; init; }
    [Input] public int MaxWords { get; init; }
    [Output] public string Note { get; init; } = "";

    /// <summary>Word count of the current draft. Computed, so loop predicates can read it by name.</summary>
    public int Words => WordCount.Of(Draft);
}

[Signature("Write a headline of at most ten words for the release, based on its notes.")]
public sealed record HeadlineSig : ISignature
{
    [Input] public required string Notes { get; init; }
    [Output] public string Headline { get; init; } = "";
}

[Signature("Review the release notes for tone and for missing migration advice. Reply with approve or revise and a reason.")]
public sealed record ReviewSig : ISignature
{
    [Input] public required string Role { get; init; }
    [Input] public required string Conversation { get; init; }
    [Output] public string Opinion { get; init; } = "";
}

[Signature("Check the release note against the change record. Use the change_details tool to read the record.")]
public sealed record FactCheckSig : ISignature
{
    [Input] public required string Note { get; init; }
    [Input] public required string ChangeId { get; init; }
    [Output] public string Verdict { get; init; } = "";
}

public static class WordCount
{
    public static int Of(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
