using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Observability.Phoenix;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ampelum.Observability.Phoenix.Tests;

public sealed class AmpelumBuilderPhoenixExtensionsTests
{
    sealed class NoopSink : ITraceSink { public void Write(TraceRecord record) { } }

    [Fact]
    public void AddPhoenixExporter_DecoratesTraceSink()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ITraceSink, NoopSink>();
        new AmpelumBuilder(s).AddPhoenixExporter(c => c.ApiKey = "k", o => o.IncludeAuditPayload = true);
        s.AddOptions();
        s.BuildServiceProvider().GetRequiredService<ITraceSink>().ShouldBeOfType<PhoenixExporter>();
    }

    [Fact]
    public void AddPhoenixExporter_NoConfigurators()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ITraceSink, NoopSink>();
        new AmpelumBuilder(s).AddPhoenixExporter();
        s.AddOptions();
        s.BuildServiceProvider().GetRequiredService<ITraceSink>().ShouldBeOfType<PhoenixExporter>();
    }

    [Fact]
    public void AddPhoenixExporter_ThrowsWithoutPriorSink()
    {
        var s = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => new AmpelumBuilder(s).AddPhoenixExporter());
    }

    [Fact]
    public void AddPhoenixExporter_ValidatesArgs()
    {
        Should.Throw<ArgumentNullException>(() => ((AmpelumBuilder)null!).AddPhoenixExporter());
    }

    [Fact]
    public void PhoenixActivitySourceName_IsExpected()
    {
        PhoenixActivitySource.Name.ShouldBe("SignalGrid.Ampelum.Observability.Phoenix");
        PhoenixActivitySource.Source.Name.ShouldBe("SignalGrid.Ampelum.Observability.Phoenix");
    }
}
