using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AmpelumBuilderSemanticCacheExtensionsTests
{
    static ServiceCollection BaseServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        sc.AddSingleton<IChatClient>(new FakeChatClient());
        return sc;
    }

    [Fact]
    public void AddSemanticCache_NullBuilder_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            AmpelumBuilderSemanticCacheExtensions.AddSemanticCache(null!));

    [Fact]
    public void AddSemanticCache_RegistersCacheAndHook()
    {
        var builder = BaseServices().AddAmpelum();
        builder.AddSemanticCache(capacity: 32);
        var sp = builder.Services.BuildServiceProvider();

        sp.GetRequiredService<ISemanticCache>().ShouldBeOfType<InMemorySemanticCache>();
        var hooks = sp.GetServices<IHook>().ToArray();
        hooks.OfType<SemanticCacheHook>().ShouldHaveSingleItem();
    }

    [Fact]
    public void AddSemanticCache_ReturnsSameBuilder()
    {
        var builder = BaseServices().AddAmpelum();
        var returned = builder.AddSemanticCache();
        returned.ShouldBeSameAs(builder);
    }
}
