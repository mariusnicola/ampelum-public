#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class HostAssemblyMarkerTests
{
    [Fact]
    public void Marker_IsInternalSealed() =>
        typeof(AssemblyMarker).IsSealed.ShouldBeTrue();
}
