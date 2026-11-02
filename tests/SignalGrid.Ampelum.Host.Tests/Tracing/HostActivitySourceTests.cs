#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Tests.Tracing;

public sealed class HostActivitySourceTests
{
    [Fact]
    public void Source_Name_IsCorrect()
    {
        HostActivitySource.Source.Name.ShouldBe("SignalGrid.Ampelum.Host");
    }

    [Fact]
    public void Source_Version_IsCorrect()
    {
        HostActivitySource.Source.Version.ShouldBe("0.7.0");
    }

    [Fact]
    public void EndpointNames_All_Has20Entries()
    {
        HostActivitySource.EndpointNames.All.Count.ShouldBe(20);
    }

    [Fact]
    public void EndpointNames_All_HasNoDuplicates()
    {
        var set = new HashSet<string>(HostActivitySource.EndpointNames.All);
        set.Count.ShouldBe(HostActivitySource.EndpointNames.All.Count);
    }

    [Fact]
    public void EndpointNames_All_ContainsAllConstants()
    {
        var all = HostActivitySource.EndpointNames.All;
        all.ShouldContain(HostActivitySource.EndpointNames.Forward);
        all.ShouldContain(HostActivitySource.EndpointNames.Stream);
        all.ShouldContain(HostActivitySource.EndpointNames.ModulesList);
        all.ShouldContain(HostActivitySource.EndpointNames.ModuleGet);
        all.ShouldContain(HostActivitySource.EndpointNames.AuditRead);
        all.ShouldContain(HostActivitySource.EndpointNames.AuditVerify);
        all.ShouldContain(HostActivitySource.EndpointNames.AuditGet);
        all.ShouldContain(HostActivitySource.EndpointNames.AuditReplay);
        all.ShouldContain(HostActivitySource.EndpointNames.PromptsList);
        all.ShouldContain(HostActivitySource.EndpointNames.PromptsModule);
        all.ShouldContain(HostActivitySource.EndpointNames.PromptsActive);
        all.ShouldContain(HostActivitySource.EndpointNames.PromptsCreate);
        all.ShouldContain(HostActivitySource.EndpointNames.PromptsPromote);
        all.ShouldContain(HostActivitySource.EndpointNames.OptimizeStart);
        all.ShouldContain(HostActivitySource.EndpointNames.OptimizeStatus);
        all.ShouldContain(HostActivitySource.EndpointNames.OptimizeStream);
        all.ShouldContain(HostActivitySource.EndpointNames.Health);
        all.ShouldContain(HostActivitySource.EndpointNames.Ready);
        all.ShouldContain(HostActivitySource.EndpointNames.OpenApi);
        all.ShouldContain(HostActivitySource.EndpointNames.AdminRebuild);
    }
}
