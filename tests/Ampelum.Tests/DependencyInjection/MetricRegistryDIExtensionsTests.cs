using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class MetricRegistryDIExtensionsTests
{
    sealed record QA : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    [Fact]
    public void AddMetricRegistry_Configures_And_Registers()
    {
        var services = new ServiceCollection();
        var b = services.AddAmpelum();
        b.AddMetricRegistry(r => r.Add(new ExactMatch<QA>("A")));
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IMetricRegistry>();
        registry.Names.ShouldContain("exact_match[A]");
    }

    [Fact]
    public void AddMetricRegistry_Replaces_Prior_Singleton()
    {
        var services = new ServiceCollection();
        var b = services.AddAmpelum();
        b.AddMetricRegistry(r => r.Add(new ExactMatch<QA>("A")));
        b.AddMetricRegistry(r => r.Add(new F1<QA>("A")));
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IMetricRegistry>();
        registry.Names.Count.ShouldBe(1);
        registry.Names.ShouldContain("f1[A]");
    }

    [Fact]
    public void AddMetricRegistry_NullBuilder_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            MetricRegistryDIExtensions.AddMetricRegistry(null!, _ => { }));

    [Fact]
    public void AddMetricRegistry_NullConfigure_Throws()
    {
        var services = new ServiceCollection();
        var b = services.AddAmpelum();
        Should.Throw<ArgumentNullException>(() => b.AddMetricRegistry(null!));
    }
}
