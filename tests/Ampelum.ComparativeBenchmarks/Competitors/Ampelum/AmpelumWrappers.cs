using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.Ampelum;

[Signature("Answer the question based on context.")]
public sealed record ConciergeQaSig : ISignature
{
    [Input] public required string Context { get; init; }
    [Input] public required string Question { get; init; }
    [Output] public string? Answer { get; set; }
}

[Signature("Rank topics and generate a brief.")]
public sealed record ContentOpsRankSig : ISignature
{
    [Input] public required string Topics { get; init; }
    [Output] public string? Ranking { get; set; }
    [Output] public string? Brief { get; set; }
}

[Signature("Generate HTML from a design brief.")]
public sealed record DesignGenSig : ISignature
{
    [Input] public required string Brief { get; init; }
    [Input] public required string BrandKit { get; init; }
    [Output] public string? Html { get; set; }
}

public static class AmpelumConcierge
{
    public static Predict<ConciergeQaSig> BuildPipeline(IChatClient client)
        => Predict.Create<ConciergeQaSig>(client);

    public static Task<Prediction<ConciergeQaSig>> RunAsync(
        Predict<ConciergeQaSig> pipeline, string context, string question)
        => pipeline.ForwardAsync(new ConciergeQaSig { Context = context, Question = question });
}

public static class AmpelumContentOps
{
    public static Predict<ContentOpsRankSig> BuildPipeline(IChatClient client)
        => Predict.Create<ContentOpsRankSig>(client);

    public static Task<Prediction<ContentOpsRankSig>> RunAsync(
        Predict<ContentOpsRankSig> pipeline, string topics)
        => pipeline.ForwardAsync(new ContentOpsRankSig { Topics = topics });
}

public static class AmpelumDesign
{
    public static Predict<DesignGenSig> BuildPipeline(IChatClient client)
        => Predict.Create<DesignGenSig>(client);

    public static Task<Prediction<DesignGenSig>> RunAsync(
        Predict<DesignGenSig> pipeline, string brief, string brandKit)
        => pipeline.ForwardAsync(new DesignGenSig { Brief = brief, BrandKit = brandKit });
}
