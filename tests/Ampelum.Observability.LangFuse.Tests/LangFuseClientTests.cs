using Xunit;
using System.Net;
using SignalGrid.Ampelum.Observability.LangFuse;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ampelum.Observability.LangFuse.Tests;

public sealed class LangFuseClientTests
{
    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            if (request.Content is { } c) LastBody = await c.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-04-25T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static LangFuseClient Build(StubHandler h, FakeTime? time = null)
    {
        var http = new HttpClient(h);
        return new LangFuseClient(
            http,
            Options.Create(new LangFuseCredentials { PublicKey = "pk", SecretKey = "sk" }),
            Options.Create(new LangFuseExporterOptions()),
            time ?? new FakeTime());
    }

    static LangFuseEvent[] Sample() => new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(), true).ToArray();

    [Fact]
    public async Task SubmitAsync_PostsWithBasicAuth()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var c = Build(h);
        await c.SubmitAsync(Sample(), TestContext.Current.CancellationToken);
        h.Last!.Method.ShouldBe(HttpMethod.Post);
        h.Last.RequestUri!.ToString().ShouldEndWith("api/public/ingestion");
        h.Last.Headers.Authorization!.Scheme.ShouldBe("Basic");
        h.Last.Headers.Authorization.Parameter.ShouldNotBeNullOrEmpty();
        h.LastBody!.ShouldContain("\"batch\"");
    }

    [Fact]
    public async Task SubmitAsync_TripsCircuitOn401()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var time = new FakeTime();
        var c = Build(h, time);
        await Should.ThrowAsync<HttpRequestException>(() => c.SubmitAsync(Sample(), TestContext.Current.CancellationToken));
        // Subsequent call within cooldown returns silently without HTTP attempt.
        h.Calls.ShouldBe(1);
        await c.SubmitAsync(Sample(), TestContext.Current.CancellationToken);
        h.Calls.ShouldBe(1);
        // After 60s the circuit closes and we attempt again.
        time.Now = time.Now.AddSeconds(61);
        await Should.ThrowAsync<HttpRequestException>(() => c.SubmitAsync(Sample(), TestContext.Current.CancellationToken));
        h.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task SubmitAsync_TripsCircuitOn403()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var c = Build(h);
        await Should.ThrowAsync<HttpRequestException>(() => c.SubmitAsync(Sample(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_ThrowsOn500WithoutCircuitTrip()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var c = Build(h);
        await Should.ThrowAsync<HttpRequestException>(() => c.SubmitAsync(Sample(), TestContext.Current.CancellationToken));
        // Circuit not tripped: a follow-up call goes through.
        await Should.ThrowAsync<HttpRequestException>(() => c.SubmitAsync(Sample(), TestContext.Current.CancellationToken));
        h.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task SubmitAsync_NoOpOnEmptyBatch()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var c = Build(h);
        await c.SubmitAsync(Array.Empty<LangFuseEvent>(), TestContext.Current.CancellationToken);
        h.Calls.ShouldBe(0);
    }

    [Fact]
    public void SubmitAsync_ThrowsOnNull()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var c = Build(h);
        Should.Throw<ArgumentNullException>(() => c.SubmitAsync(null!, default));
    }

    [Fact]
    public void Constructor_ValidatesArgs()
    {
        var creds = Options.Create(new LangFuseCredentials());
        var opts = Options.Create(new LangFuseExporterOptions());
        Should.Throw<ArgumentNullException>(() => new LangFuseClient(null!, creds, opts));
        Should.Throw<ArgumentNullException>(() => new LangFuseClient(new HttpClient(), null!, opts));
        Should.Throw<ArgumentNullException>(() => new LangFuseClient(new HttpClient(), creds, null!));
    }

    [Fact]
    public void SerializeBatch_EmitsBatchKey()
    {
        var json = LangFuseClient.SerializeBatch(new LangFuseIngestionRequest { Batch = Sample() });
        json.ShouldContain("\"batch\"");
        json.ShouldContain("trace-create");
        json.ShouldContain("generation-create");
    }
}
