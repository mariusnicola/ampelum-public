using System.Diagnostics;
using System.Diagnostics.Metrics;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class DiagnosticsTests
{
    [Fact] public void Source_IsNamedSignalGridAmpelum() => Diagnostics.Source.Name.ShouldBe("SignalGrid.Ampelum");
    [Fact] public void Meter_IsNamedSignalGridAmpelum() => Diagnostics.Meter.Name.ShouldBe("SignalGrid.Ampelum");
    [Fact] public void ModuleForwardDuration_Exists() => Diagnostics.ModuleForwardDuration.Name.ShouldBe("SignalGrid.Ampelum.module.forward.duration");
    [Fact] public void ModuleForwardErrors_Exists() => Diagnostics.ModuleForwardErrors.Name.ShouldBe("SignalGrid.Ampelum.module.forward.errors");
    [Fact] public void TokenCounters_Exist()
    {
        Diagnostics.TokensInput.Name.ShouldBe("SignalGrid.Ampelum.lm.tokens.input");
        Diagnostics.TokensOutput.Name.ShouldBe("SignalGrid.Ampelum.lm.tokens.output");
    }
    [Fact] public void AdapterParseFailures_Exists() => Diagnostics.AdapterParseFailures.Name.ShouldBe("SignalGrid.Ampelum.adapter.parse.failures");
    [Fact] public void LmCallDuration_Exists() => Diagnostics.LmCallDuration.Name.ShouldBe("SignalGrid.Ampelum.lm.call.duration");
}
