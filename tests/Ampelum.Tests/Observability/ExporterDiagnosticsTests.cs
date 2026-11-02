using SignalGrid.Ampelum.Observability;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Observability;

#pragma warning disable AMPELUM_V09_PREVIEW

public sealed class ExporterDiagnosticsTests
{
    [Fact]
    public void Counters_AreNonNullAndDistinct()
    {
        ExporterDiagnostics.ExporterDropped.ShouldNotBeNull();
        ExporterDiagnostics.ExporterFailures.ShouldNotBeNull();
        ExporterDiagnostics.ExporterAuthFailures.ShouldNotBeNull();
        ExporterDiagnostics.ExporterDropped.Name.ShouldBe("SignalGrid.Ampelum.exporter.dropped");
        ExporterDiagnostics.ExporterFailures.Name.ShouldBe("SignalGrid.Ampelum.exporter.failures");
        ExporterDiagnostics.ExporterAuthFailures.Name.ShouldBe("SignalGrid.Ampelum.exporter.auth_failures");
    }

    [Fact]
    public void RecordExporterDrop_DoesNotThrow()
    {
        Should.NotThrow(() => ExporterDiagnostics.RecordExporterDrop("test"));
    }

    [Fact]
    public void RecordExporterFailure_DoesNotThrow()
    {
        Should.NotThrow(() => ExporterDiagnostics.RecordExporterFailure("test", new InvalidOperationException("x")));
    }

    [Fact]
    public void RecordExporterAuthFailure_DoesNotThrow()
    {
        Should.NotThrow(() => ExporterDiagnostics.RecordExporterAuthFailure("test"));
    }
}

#pragma warning restore AMPELUM_V09_PREVIEW
