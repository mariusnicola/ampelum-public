using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.RateLimiting;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.RateLimiting;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AmpelumBuilderRateLimitingExtensionsTests
{
    static AmpelumBuilder NewBuilder() => new(new ServiceCollection());

    [Fact]
    public void AddRateLimit_NullBuilder_Throws()
    {
        Should.Throw<ArgumentNullException>(() => AmpelumBuilderRateLimitingExtensions.AddRateLimit(null!));
    }

    [Fact]
    public void AddRateLimit_NoExistingClient_Throws()
    {
        var builder = NewBuilder();
        Should.Throw<InvalidOperationException>(() => builder.AddRateLimit());
    }

    [Fact]
    public void AddRateLimit_WrapsExistingClient_Instance()
    {
        var inner = new FakeChatClient();
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient>(inner);

        builder.AddRateLimit(new RateLimitingOptions { RequestsPerMinute = 30 });

        var sp = builder.Services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IChatClient>();
        resolved.ShouldBeOfType<RateLimitedChatClient>();
    }

    [Fact]
    public void AddRateLimit_WrapsExistingClient_Factory()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient>(_ => new FakeChatClient());

        builder.AddRateLimit();

        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<RateLimitedChatClient>();
    }

    [Fact]
    public void AddRateLimit_WrapsExistingClient_Type()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient, FakeChatClient>();

        builder.AddRateLimit();

        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<RateLimitedChatClient>();
    }

    [Fact]
    public void AddRateLimit_DefaultOptions_Wires()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient>(new FakeChatClient());
        builder.AddRateLimit();
        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<RateLimitedChatClient>();
    }
}
