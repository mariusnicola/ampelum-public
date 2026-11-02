using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.Shared;

/// <summary>
/// LLM-as-judge scoring for W1 (Concierge) and W2 (ContentOps) where answer
/// quality is subjective. Uses a separate judge LLM call via OpenRouter.
/// For deterministic smoke tests, use <see cref="SmokeScore"/>.
/// </summary>
public static class LlmJudge
{
    /// <summary>
    /// Deterministic fake score for smoke-test runs (no real LLM). Returns
    /// 8.0 for any non-empty answer, so token_efficiency is comparable.
    /// </summary>
    public static double SmokeScore(string answer)
        => string.IsNullOrWhiteSpace(answer) ? 0 : 8.0;

    /// <summary>
    /// Scores an answer from 1–10 using a separate LLM-as-judge call.
    /// Expects <c>IChatClient</c> wired to OpenRouter with a cheap model
    /// (e.g. gpt-4o-mini). Returns 5.0 on any failure.
    /// </summary>
    public static async Task<double> ScoreAsync(
        System.Threading.Tasks.Task<IChatClient> judgeClientFactory,
        string criteria,
        string question,
        string answer,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(answer)) return 0;

        try
        {
            var client = await judgeClientFactory;
            var prompt = $@"You are an impartial judge scoring answer quality from 1 to 10.

Criteria: {criteria}
Question: {question}
Answer: {answer}

Respond with ONLY a JSON object: {{""score"": <number 1-10>, ""reasoning"": ""<one sentence>""}}";

            var messages = new[] { new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User, prompt) };
            var response = await client.GetResponseAsync(messages, null, ct);
            var text = response.Text ?? "";

            var score = ParseScore(text);
            return score;
        }
        catch
        {
            return 5.0; // Fail-safe neutral score
        }
    }

    /// <summary>
    /// Extracts the numeric score from LLM JSON response. Robust to
    /// markdown fences, extra text, and missing fields.
    /// </summary>
    private static double ParseScore(string response)
    {
        // Strip markdown fences
        var json = response
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("score", out var scoreEl) &&
                scoreEl.TryGetDouble(out var score))
            {
                return Math.Clamp(score, 1, 10);
            }
        }
        catch (JsonException)
        {
            // Try regex fallback for non-JSON responses
            var match = System.Text.RegularExpressions.Regex.Match(
                response, @"\b([0-9]|10)\b");
            if (match.Success && double.TryParse(match.Value, out var s))
                return Math.Clamp(s, 1, 10);
        }

        return 5.0;
    }

    /// <summary>
    /// Computes token efficiency: output_tokens / quality_score.
    /// Lower is better — fewer tokens for higher quality.
    /// </summary>
    public static double TokenEfficiency(int outputTokens, double score)
        => score > 0 ? outputTokens / score : double.PositiveInfinity;
}
