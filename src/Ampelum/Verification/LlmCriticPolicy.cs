using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum;

/// <summary>
/// An LLM-based critic that uses a language model to evaluate the quality of generator outputs.
/// Optionally accepts a dedicated critic client; if none is provided, uses the client from
/// the ambient <see cref="AmpelumContext"/>.
/// </summary>
/// <typeparam name="TSig">The signature type being predicted.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LlmCriticPolicy<TSig> : ICriticPolicy<TSig> where TSig : class
{
    private readonly IChatClient? _criticClient;

    /// <summary>
    /// Initializes an LLM critic with an optional dedicated client.
    /// </summary>
    /// <param name="criticClient">If null, the critic will use context.Chat from the ambient context.</param>
    public LlmCriticPolicy(IChatClient? criticClient = null)
    {
        _criticClient = criticClient;
    }

    /// <summary>
    /// Sends the input and output to an LLM and parses the response as approval or rejection.
    /// Expects the LLM to respond with lines starting with "APPROVED:" or "REJECTED:".
    /// </summary>
    public async Task<CriticVerdict> EvaluateAsync(
        Example input,
        Example generatorOutput,
        AmpelumContext context,
        CancellationToken ct = default)
    {
        var client = _criticClient ?? context.Chat;
        // Route the critic call through the hook pipeline exactly like Predict does, while
        // preserving the dedicated critic client when one was supplied (HookedLmCall always
        // calls context.Chat, so a context override is needed when _criticClient != context.Chat).
        var callContext = ReferenceEquals(client, context.Chat) ? context : context with { Chat = client };

        var inputSummary = string.Join(", ", input.Inputs.Select(kv => $"{kv.Key}={kv.Value}"));
        var outputSummary = string.Join(", ", generatorOutput.Outputs.Select(kv => $"{kv.Key}={kv.Value}"));

        var prompt = $"""
            You are a quality critic evaluating the output of an AI system.

            Input: {inputSummary}
            Output: {outputSummary}

            Evaluate whether the output is correct, complete, and high quality.
            Respond with exactly one of:
            APPROVED: <one sentence reason>
            REJECTED: <one sentence reason>
            """;

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var response = await HookedLmCall.InvokeAsync(callContext, "LlmCriticPolicy.Evaluate", messages, cancellationToken: ct).ConfigureAwait(false);
        var text = response.Text?.Trim() ?? "";

        if (text.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            var reasoning = text.Length > 9 ? text[9..].TrimStart(':', ' ') : "Approved by LLM critic.";
            return new CriticVerdict(Approved: true, Reasoning: reasoning);
        }

        var rejectionReason = text.Length > 9 ? text[9..].TrimStart(':', ' ') : "Rejected by LLM critic.";
        return new CriticVerdict(
            Approved: false,
            Reasoning: rejectionReason,
            RejectionReason: CriticRejectionReason.QualityBelowThreshold,
            Suggestions: new[] { "Review and improve the output quality." }
        );
    }
}
