using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Observability.LangFuse;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ampelum.Observability.LangFuse.Tests;

public sealed class LangFuseExporterTests
{
    sealed class CapturingSink : ITraceSink
    {
        public List<TraceRecord> Written { get; } = new();
        public Exception? Throw { get; set; }
        public void Write(TraceRecord r) { if (Throw is not null) throw Throw; Written.Add(r); }
    }

    static (LangFuseExporter exp, CapturingSink inner, RecordingLangFuseClient client) Build(
        LangFuseExporterOptions? opts = null)
    {
        var inner = new CapturingSink();
        var client = new RecordingLangFuseClient();
        var exp = new LangFuseExporter(inner, client, new LangFuseEventMapper(),
            Options.Create(opts ?? new LangFuseExporterOptions { MaxBatchSize = 4, QueueCapacity = 32 }));
        return (exp, inner, client);
    }

    [Fact]
    public async Task Write_ForwardsToInnerAndEnqueues()
    {
        var (exp, inner, client) = Build();
        var rec = TraceRecordBuilder.Sample();
        exp.Write(rec);
        await exp.DisposeAsync();
        inner.Written.ShouldContain(rec);
        client.Submissions.SelectMany(s => s).Count().ShouldBe(2); // trace + generation
    }

    [Fact]
    public async Task Write_SwallowsInnerSinkException()
    {
        var (exp, inner, _) = Build();
        inner.Throw = new InvalidOperationException("x");
        Should.NotThrow(() => exp.Write(TraceRecordBuilder.Sample()));
        await exp.DisposeAsync();
    }

    [Fact]
    public async Task Write_FlushesAtBatchBoundary()
    {
        var (exp, _, client) = Build(new LangFuseExporterOptions { MaxBatchSize = 4, QueueCapacity = 64 });
        for (int i = 0; i < 4; i++) exp.Write(TraceRecordBuilder.Sample());
        await exp.DisposeAsync();
        client.Submissions.SelectMany(s => s).Count().ShouldBe(8);
    }

    [Fact]
    public async Task Write_ContinuesWhenClientThrows()
    {
        var (exp, _, client) = Build();
        client.OnSubmit = _ => throw new HttpRequestException("nope");
        exp.Write(TraceRecordBuilder.Sample());
        await exp.DisposeAsync();
    }

    [Fact]
    public void Write_ThrowsOnNull()
    {
        var (exp, _, _) = Build();
        Should.Throw<ArgumentNullException>(() => exp.Write(null!));
        exp.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DrainsQueue()
    {
        var (exp, _, client) = Build();
        for (int i = 0; i < 3; i++) exp.Write(TraceRecordBuilder.Sample());
        await exp.DisposeAsync();
        client.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Dispose_SyncBlocks()
    {
        var (exp, _, client) = Build();
        exp.Write(TraceRecordBuilder.Sample());
        exp.Dispose();
        client.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Constructor_ValidatesArguments()
    {
        var inner = new CapturingSink();
        var client = new RecordingLangFuseClient();
        var mapper = new LangFuseEventMapper();
        var opts = Options.Create(new LangFuseExporterOptions());
        Should.Throw<ArgumentNullException>(() => new LangFuseExporter(null!, client, mapper, opts));
        Should.Throw<ArgumentNullException>(() => new LangFuseExporter(inner, null!, mapper, opts));
        Should.Throw<ArgumentNullException>(() => new LangFuseExporter(inner, client, null!, opts));
        Should.Throw<ArgumentNullException>(() => new LangFuseExporter(inner, client, mapper, null!));
        await Task.CompletedTask;
    }
}
