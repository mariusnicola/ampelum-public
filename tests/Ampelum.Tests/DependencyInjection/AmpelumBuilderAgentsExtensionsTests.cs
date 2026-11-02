using System;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Agents.Tools;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Realtime;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.DependencyInjection;

public class AmpelumBuilderAgentsExtensionsTests
{
    [Fact]
    public void AddAgents_registers_registry_and_selector()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddAmpelum().AddAgents();
        var sp = services.BuildServiceProvider();
        sp.GetService<IAgentToolRegistry>().ShouldNotBeNull();
        sp.GetService<ISpeakerSelector>().ShouldBeOfType<RoundRobinSelector>();
    }

    [Fact]
    public void AddAgents_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddAmpelum().AddAgents().AddAgents();
        var registryDescriptors = 0;
        foreach (var sd in services)
            if (sd.ServiceType == typeof(IAgentToolRegistry)) registryDescriptors++;
        registryDescriptors.ShouldBe(1);
    }

    [Fact]
    public void AddConversationMemory_registers_memory_impl()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddSingleton<InMemoryConversationMemory>(_ => new InMemoryConversationMemory(10));
        services.AddAmpelum()
            .AddAgents()
            .AddConversationMemory<InMemoryConversationMemory>();
        var sp = services.BuildServiceProvider();
        sp.GetService<IConversationMemory>().ShouldBeOfType<InMemoryConversationMemory>();
    }

    [Fact]
    public void AddRealtimeChatClient_registers_singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddAmpelum()
            .AddAgents()
            .AddRealtimeChatClient(new Uri("ws://x"), "k");
        var sp = services.BuildServiceProvider();
        var rt = sp.GetService<RealtimeChatClient>();
        rt.ShouldNotBeNull();
    }

    [Fact]
    public void Null_args_throw()
    {
        AmpelumBuilder? b = null;
        Should.Throw<ArgumentNullException>(() => b!.AddAgents());
        Should.Throw<ArgumentNullException>(() => b!.AddConversationMemory<InMemoryConversationMemory>());
        Should.Throw<ArgumentNullException>(() => b!.AddRealtimeChatClient(new Uri("ws://x"), "k"));

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        var ok = services.AddAmpelum();
        Should.Throw<ArgumentNullException>(() => ok.AddRealtimeChatClient(null!, "k"));
        Should.Throw<ArgumentNullException>(() => ok.AddRealtimeChatClient(new Uri("ws://x"), null!));
    }
}
