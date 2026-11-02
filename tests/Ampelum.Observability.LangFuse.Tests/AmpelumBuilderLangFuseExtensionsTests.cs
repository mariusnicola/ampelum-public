using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Observability.LangFuse;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ampelum.Observability.LangFuse.Tests;

public sealed class AmpelumBuilderLangFuseExtensionsTests
{
    sealed class NoopSink : ITraceSink { public void Write(TraceRecord record) { } }

    [Fact]
    public void AddLangFuseExporter_DecoratesTraceSink()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ITraceSink, NoopSink>();
        new AmpelumBuilder(s).AddLangFuseExporter(c => { c.PublicKey = "pk"; c.SecretKey = "sk"; },
                                                  o => o.IncludeAuditPayload = true);
        s.AddOptions();
        var sp = s.BuildServiceProvider();
        sp.GetRequiredService<ITraceSink>().ShouldBeOfType<LangFuseExporter>();
    }

    [Fact]
    public void AddLangFuseExporter_NoOptionsConfigurator()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ITraceSink, NoopSink>();
        new AmpelumBuilder(s).AddLangFuseExporter(c => { c.PublicKey = "pk"; c.SecretKey = "sk"; });
        s.AddOptions();
        s.BuildServiceProvider().GetRequiredService<ITraceSink>().ShouldBeOfType<LangFuseExporter>();
    }

    [Fact]
    public void AddLangFuseExporter_ThrowsWithoutPriorSink()
    {
        var s = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() =>
            new AmpelumBuilder(s).AddLangFuseExporter(c => { c.PublicKey = "p"; c.SecretKey = "s"; }));
    }

    [Fact]
    public void AddLangFuseExporter_ValidatesArgs()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ITraceSink, NoopSink>();
        var b = new AmpelumBuilder(s);
        Should.Throw<ArgumentNullException>(() => b.AddLangFuseExporter(null!));
        Should.Throw<ArgumentNullException>(() => ((AmpelumBuilder)null!).AddLangFuseExporter(c => { }));
    }
}
