using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Batch;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Resilience;
using SignalGrid.Ampelum.Retry;
using SignalGrid.Ampelum.Routing;

namespace Ampelum.Samples.BulkDocuments;

/// <summary>
/// Many documents through a batch client, single documents through a routed, rate limited, cached chat stack.
/// The cache is exact match: the same prompt text and options hit, a reworded prompt misses.
/// </summary>
public sealed class BulkDocumentsPipeline : IDisposable
{
    readonly IBatchChatClient _batch;
    readonly BulkDocumentsOptions _options;
    readonly ServiceProvider _provider;

    public BulkDocumentsPipeline(
        IBatchChatClient batch,
        IReadOnlyList<ProviderEndpoint> endpoints,
        IDistributedCache cache,
        BulkDocumentsOptions options)
    {
        _batch = batch;
        _options = options;

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var builder = services.AddAmpelum();

        // Each endpoint gets its own breaker so one broken provider is skipped while the others keep serving.
        foreach (var endpoint in endpoints)
            builder.AddProviderEndpoint(endpoint with { Client = new CircuitBreakerChatClient(endpoint.Client, options.Breaker) });

        // Stack, innermost first: breaker per endpoint, router, rate limit, cache. The cache is outermost so a hit
        // neither spends a rate limit token nor reaches a provider. AddRouting replaces any earlier IChatClient.
        switch (options.Routing)
        {
            case RoutingMode.Failover: builder.AddRouting<Failover>(); break;
            case RoutingMode.LoadBalanced: builder.AddRouting<LoadBalanced>(); break;
            case RoutingMode.KeyRotating:
                builder.AddRouting<KeyRotating>();
                // AddRouting builds the strategy with its default of one call per key; replace it to honour the option.
                services.RemoveAll<IRoutingStrategy>();
                services.AddSingleton<IRoutingStrategy>(new KeyRotating(options.KeyRotatingCallsPerKey));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(options), options.Routing, "Unknown routing mode.");
        }
        builder.AddRateLimit(options.RateLimit);
        builder.WithResponseCache(cache);

        // Re-asks the model with the parse error when a reply does not fit the signature.
        services.AddSingleton<IHook>(new RetryHook(maxRecoveryAttempts: options.ParseRecoveryAttempts));
        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Submits the documents as one batch and maps results back by custom id, so provider ordering does not matter.
    /// A document that fails is submitted again in the next attempt; one that never succeeds keeps its error.
    /// </summary>
    public async Task<IReadOnlyList<DocumentSummary>> SummarizeBatchAsync(
        IReadOnlyList<Document> documents, CancellationToken ct = default)
    {
        var done = new Dictionary<string, DocumentSummary>();
        var lastError = new Dictionary<string, string>();
        var attempts = 0;

        async Task<IReadOnlyList<DocumentSummary>> Attempt(IReadOnlyList<string> feedback, CancellationToken token)
        {
            attempts++;
            var pending = documents.Where(d => !done.ContainsKey(d.Id)).ToList();
            foreach (var item in await RunBatchAsync(pending, token))
            {
                if (item.Summary is not null)
                    done[item.DocumentId] = item;
                else
                    lastError[item.DocumentId] = item.Error ?? "no result";
            }
            var missing = documents.Where(d => !done.ContainsKey(d.Id)).Select(d => d.Id).ToList();
            AmpelumAssert.Assert(missing.Count == 0, $"No summary for: {string.Join(", ", missing)}");
            return documents.Select(d => done[d.Id]).ToList();
        }

        try
        {
            return await new RetryModule<IReadOnlyList<DocumentSummary>>(Attempt, _options.BatchAttempts).ForwardAsync(ct);
        }
        catch (AssertionFailedException)
        {
            return Collect();
        }
        // A later batch that is canceled, expired or never ends must not discard summaries already received.
        // The first batch has nothing to keep, so its failure still propagates.
        catch (Exception ex) when (attempts > 1 && ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            foreach (var d in documents.Where(d => !done.ContainsKey(d.Id)))
                lastError[d.Id] = ex.Message;
            return Collect();
        }

        IReadOnlyList<DocumentSummary> Collect() => documents.Select(d => done.TryGetValue(d.Id, out var ok)
            ? ok
            : new DocumentSummary(d.Id, null, lastError.GetValueOrDefault(d.Id))).ToList();
    }

    /// <summary>One interactive summary through the cached, rate limited, routed stack.</summary>
    public async Task<string> SummarizeAsync(Document document, CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var predict = scope.ServiceProvider.GetRequiredService<Predict<SummarySignature>>();
        var prediction = await predict.ForwardAsync(new SummarySignature { Text = document.Text }, ct: ct);
        return prediction.Value.Summary;
    }

    /// <summary>
    /// The same summary as updates while the model writes. Streaming does not fail over: the router
    /// picks one endpoint per request and the circuit breaker does not guard a stream already running.
    /// </summary>
    public async IAsyncEnumerable<PredictionUpdate<SummarySignature>> StreamSummaryAsync(
        Document document, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var predict = scope.ServiceProvider.GetRequiredService<Predict<SummarySignature>>();
        await foreach (var update in predict.StreamAsync(new SummarySignature { Text = document.Text }, ct: ct))
            yield return update;
    }

    async Task<IReadOnlyList<DocumentSummary>> RunBatchAsync(IReadOnlyList<Document> documents, CancellationToken ct)
    {
        var instruction = SignatureInstruction();
        var requests = documents
            .Select(d => new BatchRequest(
                d.Id,
                [new ChatMessage(ChatRole.System, instruction), new ChatMessage(ChatRole.User, d.Text)]))
            .ToList();
        var handle = await _batch.SubmitAsync(
            requests, new ChatOptions { ModelId = _options.BatchModel, MaxOutputTokens = _options.MaxSummaryTokens }, ct);

        for (var poll = 0; ; poll++)
        {
            var status = await _batch.GetStatusAsync(handle, ct);
            if (status.State == BatchState.Completed)
                break;
            if (status.State != BatchState.InProgress)
                throw new InvalidOperationException($"Batch {handle.Id} ended as {status.State}.");
            if (poll >= _options.MaxPolls)
                throw new TimeoutException($"Batch {handle.Id} still running after {_options.MaxPolls} status checks.");
            await Task.Delay(_options.PollInterval, _options.Time, ct);
        }

        var known = documents.Select(d => d.Id).ToHashSet();
        var results = new List<DocumentSummary>();
        await foreach (var item in _batch.StreamResultsAsync(handle, ct))
        {
            if (!known.Contains(item.CustomId))
                continue;
            results.Add(item.Response is { } response
                ? new DocumentSummary(item.CustomId, response.Text, null)
                : new DocumentSummary(item.CustomId, null, item.ErrorMessage));
        }
        return results;
    }

    // The batch API takes plain messages, so the signature's instruction is reused as the system prompt.
    static string SignatureInstruction() =>
        ((SignatureAttribute)Attribute.GetCustomAttribute(typeof(SummarySignature), typeof(SignatureAttribute))!).Instructions;

    public void Dispose() => _provider.Dispose();
}
