#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Tests.Tracing;

public sealed class HostMetricsTests
{
    [Fact]
    public void HostMetrics_Name_IsCorrect()
    {
        HostMetrics.Name.ShouldBe("SignalGrid.Ampelum.Host");
    }

    [Fact]
    public void HostMetrics_Meter_Name_IsCorrect()
    {
        HostMetrics.Meter.Name.ShouldBe("SignalGrid.Ampelum.Host");
    }

    [Fact]
    public void RequestCount_Name_IsCorrect()
    {
        HostMetrics.RequestCount.Name.ShouldBe("ampelum.host.request.count");
    }

    [Fact]
    public void RequestDurationMs_Name_IsCorrect()
    {
        HostMetrics.RequestDurationMs.Name.ShouldBe("ampelum.host.request.duration");
    }

    [Fact]
    public void SseEventsEmitted_Name_IsCorrect()
    {
        HostMetrics.SseEventsEmitted.Name.ShouldBe("ampelum.host.sse.events.emitted");
    }
}
