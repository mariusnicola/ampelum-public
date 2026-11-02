using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class UsageTrackerRegistrationTests
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

    sealed class CustomUsageTracker : IUsageTracker
    {
        public void Record(UsageDetails? usage, bool cacheHit) { }
        public UsageSummary Snapshot() => new(0, 0, 0, 0, 0, null);
        public void Reset() { }
        public IDisposable Scope(string name) => new Disposable();

        sealed class Disposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void AddDSPy_RegistersDefaultUsageTracker_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IUsageTracker>();
        tracker.ShouldBeOfType<DefaultUsageTracker>();
    }

    [Fact]
    public void WithUsageTracker_OverridesDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum().WithUsageTracker<CustomUsageTracker>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IUsageTracker>();
        tracker.ShouldBeOfType<CustomUsageTracker>();
    }

    [Fact]
    public void WithUsageTracker_IsScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum().WithUsageTracker<CustomUsageTracker>();
        using var sp = services.BuildServiceProvider();
        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var tracker1 = scope1.ServiceProvider.GetRequiredService<IUsageTracker>();
        var tracker2 = scope2.ServiceProvider.GetRequiredService<IUsageTracker>();
        tracker1.ShouldNotBe(tracker2);
    }

    [Fact]
    public void DefaultUsageTracker_IsScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient, Stub>();
        services.AddAmpelum();
        using var sp = services.BuildServiceProvider();
        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();
        var tracker1 = scope1.ServiceProvider.GetRequiredService<IUsageTracker>();
        var tracker2 = scope2.ServiceProvider.GetRequiredService<IUsageTracker>();
        tracker1.ShouldNotBe(tracker2);
    }
}
