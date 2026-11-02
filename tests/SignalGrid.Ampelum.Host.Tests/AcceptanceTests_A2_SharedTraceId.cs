#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class AcceptanceTests_A2_SharedTraceId : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ForwardRequest_SameTraceIdInAuditLog()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## A ## ]]\nHello World");

        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("What is 2+2?") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Ampelum-Trace-Id", out var traceIdHeader).ShouldBeTrue();
        traceIdHeader.ShouldNotBeEmpty();
        var traceId = traceIdHeader.First();
        traceId.Length.ShouldBe(32);

        var auditPage = await _factory.HttpClient.GetAsync("/audit?limit=100", ct);
        auditPage.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var page = await auditPage.Content.ReadFromJsonAsync<AuditPage>(
            AmpelumHostContractsJsonContext.Default.AuditPage, ct);
        page.ShouldNotBeNull();

        var matchingEntries = page.Entries.Where(e => e.TraceId == traceId).ToList();
        matchingEntries.Count.ShouldBeGreaterThan(0);

        foreach (var entry in matchingEntries)
        {
            entry.TraceId.ShouldBe(traceId, $"Entry at sequence {entry.Sequence} does not match trace ID");
        }
    }
}