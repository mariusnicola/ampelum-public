using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Reranking;

/// <summary>Options for <see cref="CrossEncoderReranker"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CrossEncoderOptions
{
    /// <summary>Filesystem path to the ONNX model. Default: <c>%LOCALAPPDATA%\Ampelum\Models\ms-marco-MiniLM-L-6-v2.onnx</c>.</summary>
    public string ModelPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ampelum", "Models", "ms-marco-MiniLM-L-6-v2.onnx");

    /// <summary>Expected SHA-256 hex of the model file (case-insensitive).</summary>
    public string ExpectedSha256 { get; init; } = "PLACEHOLDER_FILL_AT_MODEL_PIN_TIME";

    /// <summary>Maximum input sequence length passed to the model. Default 512.</summary>
    public int MaxSequenceLength { get; init; } = 512;
}
