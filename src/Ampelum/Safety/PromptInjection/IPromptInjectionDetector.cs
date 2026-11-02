using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.PromptInjection;

/// <summary>Synchronous strategy for classifying a single text fragment as prompt-injection or not.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IPromptInjectionDetector
{
    /// <summary>Scans <paramref name="text"/>; returns a structured result with rule id + excerpt when flagged.</summary>
    PromptInjectionResult Scan(string text);
}
