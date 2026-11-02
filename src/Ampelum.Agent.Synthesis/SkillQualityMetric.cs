using SignalGrid.Ampelum.Optimizers;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// Metric for evaluating skill quality based on execution history.
/// Scores a combination of success rate (70%) and user feedback (30%).
/// Returns a score in [0, 1].
/// </summary>
public sealed class SkillQualityMetric : IMetric
{
    private readonly IReadOnlyList<SkillExecutionRecord> _history;

    public SkillQualityMetric(IReadOnlyList<SkillExecutionRecord> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    public double Score(object actual, object expected, SignatureMetadata signature)
    {
        // Primary signal: success rate from history.
        // If no history, return neutral score (0.5).
        double successRate = _history.Count == 0 ? 0.5
            : (double)_history.Count(r => r.Succeeded) / _history.Count;

        // Secondary signal: user feedback (positive = 1.0, negative = 0.0, null = neutral = 0.5).
        double feedbackScore = 0.5;
        var feedbackRecords = _history.Where(r => r.UserFeedback != null).ToList();
        if (feedbackRecords.Count > 0)
        {
            // Positive feedback: contains "good", "great", "excellent", "yes", "approved" (case-insensitive).
            // Negative feedback: everything else with non-null feedback.
            feedbackScore = (double)feedbackRecords.Count(r =>
                r.UserFeedback!.Contains("good", StringComparison.OrdinalIgnoreCase) ||
                r.UserFeedback!.Contains("great", StringComparison.OrdinalIgnoreCase) ||
                r.UserFeedback!.Contains("excellent", StringComparison.OrdinalIgnoreCase) ||
                r.UserFeedback!.Contains("yes", StringComparison.OrdinalIgnoreCase) ||
                r.UserFeedback!.Contains("approved", StringComparison.OrdinalIgnoreCase)
            ) / feedbackRecords.Count;
        }

        // Weighted: 70% success rate + 30% feedback.
        return 0.7 * successRate + 0.3 * feedbackScore;
    }
}
