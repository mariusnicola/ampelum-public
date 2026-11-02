using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class DiagnosticsV03Tests
{
    [Fact] public void EvaluateScore_Exists() => Diagnostics.EvaluateScore.Name.ShouldBe("SignalGrid.Ampelum.evaluate.score");
    [Fact] public void EvaluateDuration_Exists() => Diagnostics.EvaluateDuration.Name.ShouldBe("SignalGrid.Ampelum.evaluate.duration");
    [Fact] public void EvaluateFailures_Exists() => Diagnostics.EvaluateFailures.Name.ShouldBe("SignalGrid.Ampelum.evaluate.failures");
    [Fact] public void RetryAttempts_Exists() => Diagnostics.RetryAttempts.Name.ShouldBe("SignalGrid.Ampelum.retry.attempts");
    [Fact] public void RetrySucceededOnAttempt_Exists() => Diagnostics.RetrySucceededOnAttempt.Name.ShouldBe("SignalGrid.Ampelum.retry.succeeded_on_attempt");
}
