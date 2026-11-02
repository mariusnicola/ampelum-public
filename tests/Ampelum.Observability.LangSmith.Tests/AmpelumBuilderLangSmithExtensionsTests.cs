using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Observability.LangSmith;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ampelum.Observability.LangSmith.Tests;

public sealed class AmpelumBuilderLangSmithExtensionsTests
{
    sealed class NoopSink : ITraceSink { public void Write(TraceRecord record) { } }
    sealed class StubChat : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    [Fact]
    public void AddLangSmithExporter_RegistersDecoratedTraceSink()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITraceSink, NoopSink>();
        var builder = new AmpelumBuilder(services);

        builder.AddLangSmithExporter(c => { c.ApiKey = "k"; c.ProjectName = "demo"; },
                                     o => o.IncludeAuditPayload = true);

        services.AddOptions();
        var sp = services.BuildServiceProvider();
        var sink = sp.GetRequiredService<ITraceSink>();
        sink.ShouldBeOfType<LangSmithExporter>();
    }

    [Fact]
    public void AddLangSmithExporter_WorksWithoutOptionsConfigurator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITraceSink, NoopSink>();
        var builder = new AmpelumBuilder(services);
        builder.AddLangSmithExporter(c => { c.ApiKey = "k"; c.ProjectName = "demo"; });
        services.AddOptions();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ITraceSink>().ShouldBeOfType<LangSmithExporter>();
    }

    [Fact]
    public void AddLangSmithExporter_ThrowsWhenNoPriorTraceSink()
    {
        var services = new ServiceCollection();
        var builder = new AmpelumBuilder(services);
        Should.Throw<InvalidOperationException>(() =>
            builder.AddLangSmithExporter(c => { c.ApiKey = "k"; c.ProjectName = "p"; }));
    }

    [Fact]
    public void AddLangSmithExporter_ValidatesArguments()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITraceSink, NoopSink>();
        var builder = new AmpelumBuilder(services);
        Should.Throw<ArgumentNullException>(() => builder.AddLangSmithExporter(null!));
        Should.Throw<ArgumentNullException>(() =>
            ((AmpelumBuilder)null!).AddLangSmithExporter(c => { c.ApiKey = "k"; c.ProjectName = "p"; }));
    }
}
