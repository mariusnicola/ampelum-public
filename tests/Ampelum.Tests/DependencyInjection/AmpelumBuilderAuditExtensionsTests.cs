using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.DependencyInjection;

public sealed class AmpelumBuilderAuditExtensionsTests
{
    [Fact]
    public void AddAuditHook_RegistersInMemorySink_AndHookAtOrder10()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new SignalGrid.Ampelum.Testing.FakeChatClient());
        services.AddAmpelum().AddAuditHook();
        using var sp = services.BuildServiceProvider();
        var sink = sp.GetRequiredService<IAuditSink>();
        sink.ShouldBeOfType<InMemoryAuditSink>();
        var hooks = sp.GetServices<IHook>();
        hooks.OfType<AuditHook>().Single().Order.ShouldBe(10);
    }

    [Fact]
    public async Task AddBatchedAuditSink_WithAFactory_WrapsTheFileSink_AndClosesItWhenTheContainerIsDisposed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ampelum-{Guid.NewGuid():N}.jsonl");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IChatClient>(new SignalGrid.Ampelum.Testing.FakeChatClient());
            services.AddAmpelum().AddBatchedAuditSink(_ => new JsonFileAuditSink(path)).AddAuditHook();
            await using (var sp = services.BuildServiceProvider())
            {
                var sink = sp.GetRequiredService<IAuditSink>();
                sink.ShouldBeOfType<BatchedAuditSink>();
                var hook = sp.GetServices<IHook>().OfType<AuditHook>().Single();
                var ctx = new HookContext
                {
                    ModuleName = "M",
                    Inputs = new Dictionary<string, object?> { ["q"] = "hi" },
                    Outputs = new Dictionary<string, object?> { ["a"] = "ok" },
                };
                await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
            }

            // The container drained the queue and released the file: it holds the entry and can be opened exclusively.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await using var reread = new JsonFileAuditSink(path);
            reread.NextSequence.ShouldBe(1L);
            (await AuditChainVerifier.VerifyAsync(reread, TestContext.Current.CancellationToken)).IsValid.ShouldBeTrue();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task AddJsonFileAuditSink_RegistersJsonFileSink()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ampelum-{Guid.NewGuid():N}.jsonl");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IChatClient>(new SignalGrid.Ampelum.Testing.FakeChatClient());
            services.AddAmpelum().AddJsonFileAuditSink(path).AddAuditHook();
            await using (var sp = services.BuildServiceProvider())
            {
                sp.GetRequiredService<IAuditSink>().ShouldBeOfType<JsonFileAuditSink>();
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AddAuditHook_ReplayMode_PassesFlagThrough()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new SignalGrid.Ampelum.Testing.FakeChatClient());
        services.AddAmpelum().AddAuditHook(replayMode: true);
        using var sp = services.BuildServiceProvider();
        var hook = sp.GetServices<IHook>().OfType<AuditHook>().Single();
        hook.ShouldNotBeNull();
    }
}
