using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.Optimizers;

public class DiagnosticsOptimizerTests
{
    [Fact]
    public void Diagnostics_OptimizerCompileDuration_Exists()
    {
        Diagnostics.OptimizerCompileDuration.ShouldNotBeNull();
        Diagnostics.OptimizerCompileDuration.Unit.ShouldBe("ms");
    }

    [Fact]
    public void Diagnostics_OptimizerCompileExamples_Exists()
    {
        Diagnostics.OptimizerCompileExamples.ShouldNotBeNull();
        Diagnostics.OptimizerCompileExamples.Unit.ShouldBe("{examples}");
    }

    [Fact]
    public void Diagnostics_OptimizerCompileScore_Exists()
    {
        Diagnostics.OptimizerCompileScore.ShouldNotBeNull();
        Diagnostics.OptimizerCompileScore.Unit.ShouldBe("1");
    }
}
