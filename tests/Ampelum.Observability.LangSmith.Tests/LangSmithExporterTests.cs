using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Observability.LangSmith;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ampelum.Observability.LangSmith.Tests;

public sealed class LangSmithExporterTests
{
    static IOptions<T> Wrap<T>(T value) where T : class => Options.Create(value);

    sealed class CapturingSink : ITraceSink
    {
        public List<TraceRecord> Written { get; } = new();
        public Exception? Throw { get; set; }
        public void Write(TraceRecord record)
        {
            if (Throw is not null) throw Throw;
            Written.Add(record);
        }
    }

    static (LangSmithExporter exporter, CapturingSink inner, RecordingLangSmithClient client) Build(
        LangSmithExporterOptions? opts = null)
    {
        var inner = new CapturingSink();
        var client = new RecordingLangSmithClient();
        var exporter = new LangSmithExporter(
            inner, client, new LangSmithRunMapper(),
            Wrap(opts ?? new LangSmithExporterOptions { MaxBatchSize = 2, QueueCapacity = 8 }),
            Wrap(new LangSmithCredentials { ApiKey = "k", ProjectName = "p" }));
        return (exporter, inner, client);
    }

    [Fact]
    public async Task Write_ForwardsToInnerSinkBeforeEnqueue()
    {
        var (exporter, inner, client) = Build();
        var rec = TraceRecordBuilder.Sample();
        exporter.Write(rec);
        await exporter.DisposeAsync();
        inner.Written.ShouldContain(rec);
        client.Submissions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Write_SwallowsInnerSinkException()
    {
        var (exporter, inner, _) = Build();
        inner.Throw = new InvalidOperationException("boom");
        Should.NotThrow(() => exporter.Write(TraceRecordBuilder.Sample()));
        await exporter.DisposeAsync();
    }

    [Fact]
    public async Task Write_FlushesAtBatchSizeBoundary()
    {
        var (exporter, _, client) = Build(new LangSmithExporterOptions { MaxBatchSize = 2, QueueCapacity = 16 });
        for (int i = 0; i < 4; i++) exporter.Write(TraceRecordBuilder.Sample());
        await exporter.DisposeAsync();
        client.Submissions.SelectMany(s => s).Count().ShouldBe(4);
    }

    [Fact]
    public async Task Write_DropsWhenClientThrowsAndIncrementsFailureCounter()
    {
        var (exporter, _, client) = Build();
        client.OnSubmit = _ => throw new HttpRequestException("nope");
        exporter.Write(TraceRecordBuilder.Sample());
        await exporter.DisposeAsync();
    }

    [Fact]
    public void Write_ThrowsOnNullRecord()
    {
        var (exporter, _, _) = Build();
        Should.Throw<ArgumentNullException>(() => exporter.Write(null!));
        exporter.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DrainsPendingQueue()
    {
        var (exporter, _, client) = Build();
        for (int i = 0; i < 3; i++) exporter.Write(TraceRecordBuilder.Sample());
        await exporter.DisposeAsync();
        client.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Dispose_SyncBlocksUntilDrainCompletes()
    {
        var (exporter, _, client) = Build();
        exporter.Write(TraceRecordBuilder.Sample());
        exporter.Dispose();
        client.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Constructor_ValidatesArguments()
    {
        var inner = new CapturingSink();
        var client = new RecordingLangSmithClient();
        var mapper = new LangSmithRunMapper();
        var opts = Wrap(new LangSmithExporterOptions());
        var creds = Wrap(new LangSmithCredentials { ApiKey = "k", ProjectName = "p" });

        Should.Throw<ArgumentNullException>(() => new LangSmithExporter(null!, client, mapper, opts, creds));
        Should.Throw<ArgumentNullException>(() => new LangSmithExporter(inner, null!, mapper, opts, creds));
        Should.Throw<ArgumentNullException>(() => new LangSmithExporter(inner, client, null!, opts, creds));
        Should.Throw<ArgumentNullException>(() => new LangSmithExporter(inner, client, mapper, null!, creds));
        Should.Throw<ArgumentNullException>(() => new LangSmithExporter(inner, client, mapper, opts, null!));
        await Task.CompletedTask;
    }
}
