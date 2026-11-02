using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Resilience;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Resilience;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AmpelumBuilderCircuitBreakerExtensionsTests
{
    static AmpelumBuilder NewBuilder() => new(new ServiceCollection());

    [Fact]
    public void AddCircuitBreaker_NullBuilder_Throws()
    {
        Should.Throw<ArgumentNullException>(() => AmpelumBuilderCircuitBreakerExtensions.AddCircuitBreaker(null!));
    }

    [Fact]
    public void AddCircuitBreaker_NoExistingClient_Throws()
    {
        var builder = NewBuilder();
        Should.Throw<InvalidOperationException>(() => builder.AddCircuitBreaker());
    }

    [Fact]
    public void AddCircuitBreaker_WrapsInstance()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient>(new FakeChatClient());
        builder.AddCircuitBreaker(new CircuitBreakerOptions { FailureThreshold = 2 });
        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<CircuitBreakerChatClient>();
    }

    [Fact]
    public void AddCircuitBreaker_WrapsFactory()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient>(_ => new FakeChatClient());
        builder.AddCircuitBreaker();
        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<CircuitBreakerChatClient>();
    }

    [Fact]
    public void AddCircuitBreaker_WrapsType()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IChatClient, FakeChatClient>();
        builder.AddCircuitBreaker();
        var sp = builder.Services.BuildServiceProvider();
        sp.GetRequiredService<IChatClient>().ShouldBeOfType<CircuitBreakerChatClient>();
    }
}
