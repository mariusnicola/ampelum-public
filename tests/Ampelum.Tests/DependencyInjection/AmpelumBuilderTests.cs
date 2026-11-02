using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumBuilderTests
{
    [Signature("")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class Stub : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nx")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void AddDSPy_RegistersContextAndModules()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<Predict<Qa>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ChainOfThought<Qa>>().ShouldNotBeNull();
    }

    [Fact]
    public void UseAdapter_ReplacesDefaultChatAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum().UseAdapter<JsonAdapter>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().Adapter.ShouldBeOfType<JsonAdapter>();
    }

    [Fact]
    public void UseTraceSink_ReplacesDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum().UseTraceSink<InMemoryTraceSink>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().TraceSink.ShouldBeOfType<InMemoryTraceSink>();
    }

    [Fact]
    public void AddDSPy_DefaultAdapterIsChatAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().Adapter.ShouldBeOfType<ChatAdapter>();
    }

    [Fact]
    public void AddDSPy_DefaultTraceSinkIsBatchedWrappingNull()
    {
        // v0.7 default-on (spec §H4): the default ITraceSink is a BatchedTraceSink wrapping
        // NullTraceSink. UseSynchronousTraceSink<NullTraceSink>() opts out.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().TraceSink.ShouldBeOfType<BatchedTraceSink>();
    }

    [Fact]
    public void UseSynchronousTraceSink_OptsOutOfBatchedDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum().UseSynchronousTraceSink<InMemoryTraceSink>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmpelumContext>().TraceSink.ShouldBeOfType<InMemoryTraceSink>();
    }
}
