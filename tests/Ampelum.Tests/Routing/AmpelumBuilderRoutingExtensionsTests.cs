using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.Routing;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AmpelumBuilderRoutingExtensionsTests
{
    static ServiceCollection BaseServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return sc;
    }

    [Fact]
    public void AddProviderEndpoint_NullBuilder_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            AmpelumBuilderRoutingExtensions.AddProviderEndpoint(
                null!, new ProviderEndpoint("a", new FakeChatClient())));

    [Fact]
    public void AddProviderEndpoint_NullEndpoint_Throws()
    {
        var builder = BaseServices().AddAmpelum();
        Should.Throw<ArgumentNullException>(() => builder.AddProviderEndpoint(null!));
    }

    [Fact]
    public void AddRouting_NullBuilder_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            AmpelumBuilderRoutingExtensions.AddRouting<Failover>(null!));

    [Fact]
    public void AddRouting_WithoutEndpoints_ResolvingThrows()
    {
        var builder = BaseServices().AddAmpelum();
        builder.AddRouting<Failover>();
        var sp = builder.Services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public async Task AddRouting_WithEndpoints_ResolvesRoutedClient()
    {
        var sc = BaseServices();
        var inner = new FakeChatClient().EnqueueText("[[ ## A ## ]]\nrouted");
        var builder = sc.AddAmpelum();
        builder.AddProviderEndpoint(new ProviderEndpoint("primary", inner));
        builder.AddRouting<Failover>();

        var sp = sc.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClient>();
        client.ShouldBeOfType<RoutedChatClient>();

        var resp = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "q")],
            null,
            TestContext.Current.CancellationToken);
        resp.AdditionalProperties![HookMetadataKeys.RouterTargetName].ShouldBe("primary");
    }

    [Fact]
    public void AddRouting_ReplacesPriorIChatClientRegistration()
    {
        var sc = BaseServices();
        var stub = new FakeChatClient();
        sc.AddSingleton<IChatClient>(stub);
        var builder = sc.AddAmpelum();
        builder.AddProviderEndpoint(new ProviderEndpoint("a", new FakeChatClient()));
        builder.AddRouting<Failover>();

        // No two IChatClient descriptors should remain.
        sc.Count(d => d.ServiceType == typeof(IChatClient)).ShouldBe(1);
    }

    [Fact]
    public void AddRouting_ReturnsSameBuilderForChaining()
    {
        var builder = BaseServices().AddAmpelum();
        var returned = builder.AddRouting<Failover>();
        returned.ShouldBeSameAs(builder);
    }

    [Fact]
    public void AddProviderEndpoint_ReturnsSameBuilderForChaining()
    {
        var builder = BaseServices().AddAmpelum();
        var returned = builder.AddProviderEndpoint(new ProviderEndpoint("a", new FakeChatClient()));
        returned.ShouldBeSameAs(builder);
    }
}

