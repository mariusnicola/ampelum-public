using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.A2A;
using SignalGrid.Ampelum.Batch;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using SignalGrid.Ampelum.Retrieval.Loaders;

namespace Ampelum.Samples.DocumentReview;

/// <summary>A supplier file after loading. A file the loader could not read has no pages and carries the loader's error.</summary>
internal sealed record LoadedFile(string Name, IReadOnlyList<Document> Pages, string? Error = null)
{
    public string Text => string.Join(' ', Pages.Select(p => p.Text.Trim()));
}

/// <summary>A clause after reranking.</summary>
internal sealed record RankedClause(string Id, string Text, double Score);

/// <summary>The remote reviewer's answer for one clause.</summary>
internal sealed record Verdict(string ClauseId, bool Completed, string Text);

/// <summary>The four steps of the supplier document review, each on one library type.</summary>
internal sealed class ReviewDesk(ReviewSettings settings, ReviewTransport transport)
{
    /// <summary>A clause scoring below this goes to the remote reviewer. The value is a policy choice, not tuned to any vendor's scale.</summary>
    public const double AutoApproveScore = 0.5;

    public const int TopClauses = 2;

    /// <summary>Wait between status calls on the overnight batch.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    public const int MaxPolls = 300;

    // VerifyCapabilities does not use the identity provider's key, so a throwaway one is enough to build the verifier.
    static readonly DefaultAgentCardVerifier CardVerifier = new(new DefaultAgentIdentityProvider(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Loads each file page by page with <see cref="PdfLoader"/>.</summary>
    public async Task<IReadOnlyList<LoadedFile>> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var loader = new PdfLoader();
        var files = new List<LoadedFile>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            var pages = new List<Document>();
            await using var stream = File.OpenRead(path);
            string? error = null;
            try
            {
                await foreach (var page in loader.LoadAsync(stream, new DocumentLoadContext { SourceUri = name }, cancellationToken))
                    pages.Add(page);
            }
            catch (InvalidDataException ex)
            {
                // The loader says the file is unreadable; keep the reason and leave the file out of the review.
                pages.Clear();
                error = ex.Message;
            }
            files.Add(new LoadedFile(name, pages, error));
        }
        return files;
    }

    /// <summary>Ranks every loaded page against the question with the reranker of the given kind.</summary>
    public async Task<IReadOnlyList<RankedClause>> RerankAsync(
        RerankerKind kind, string key, string model, IReadOnlyList<LoadedFile> files, string question, CancellationToken cancellationToken)
    {
        var candidates = files
            .SelectMany(f => f.Pages)
            .Select(p => new RetrievedDocument(p.Id, p.Text, 0, p.Metadata))
            .ToList();

        using var http = new HttpClient(transport.Rerank, disposeHandler: false);
        IReranker reranker = kind switch
        {
            RerankerKind.Cohere => new CohereReranker(http, transport.CohereEndpoint is { } cohere
                ? new CohereRerankerOptions { ApiKey = key, Model = model, Endpoint = cohere }
                : new CohereRerankerOptions { ApiKey = key, Model = model }),
            RerankerKind.Voyage => new VoyageReranker(http, transport.VoyageEndpoint is { } voyage
                ? new VoyageRerankerOptions { ApiKey = key, Model = model, Endpoint = voyage }
                : new VoyageRerankerOptions { ApiKey = key, Model = model }),
            _ => throw new InvalidOperationException($"No reranker for {kind}."),
        };

        var ranked = await reranker.RerankAsync(question, candidates, TopClauses, cancellationToken);
        return [.. ranked.Select(r => new RankedClause(r.Id, r.Text, r.Score))];
    }

    /// <summary>Sends each clause to the remote reviewer: reads the agent card, then sends one task per clause.</summary>
    public async Task<(string AgentName, IReadOnlyList<Verdict> Verdicts)> EscalateAsync(
        IReadOnlyList<RankedClause> hardCases, string question, CancellationToken cancellationToken)
    {
        var baseUrl = settings.AgentUrl();
        // The client does not own this HttpClient, so the desk disposes it.
        using var http = new HttpClient(transport.Agent, disposeHandler: false);
        using var client = new A2AClient(http);

        // The client refuses a card whose URL is not on the origin it came from, so nothing is sent to another host.
        var card = await client.FetchAgentCardAsync(baseUrl.ToString(), cancellationToken)
            ?? throw new InvalidOperationException("The remote agent returned an empty card.");
        var rpc = new Uri(card.Url);

        // Anything outside the four capability flags stops the escalation. The sample sends no token, so the identity check is not used.
        var warnings = CardVerifier.VerifyCapabilities(card);
        if (warnings.Count > 0)
            throw new InvalidOperationException($"The agent card announces capabilities this sample does not know: {string.Join("; ", warnings.Select(w => w.Warning))}. Nothing was sent.");

        var verdicts = new List<Verdict>();
        foreach (var clause in hardCases)
        {
            var message = new A2AMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = MessageRole.User,
                Parts = [new A2APart { Text = $"Clause {clause.Id}: {clause.Text} Question: {question}" }],
            };
            var response = await client.SendMessageAsync(rpc.ToString(), message, ct: cancellationToken);
            verdicts.Add(ToVerdict(clause.Id, response));
        }
        return (card.Name, verdicts);
    }

    /// <summary>Opens the overnight summary queue on the batch API.</summary>
    public SummaryQueue OpenQueue()
    {
        var (key, model) = settings.Batch();
        var http = new HttpClient(transport.Batch, disposeHandler: false) { BaseAddress = transport.BatchBase };
        return new SummaryQueue(new OpenAIBatchSubmitter(http, key, transport.Clock), http, model, transport.Clock);
    }

    // The client returns a JSON-RPC error as a value; reading it is the caller's job.
    static Verdict ToVerdict(string clauseId, A2AResponse response)
    {
        if (response.Error is not null)
            return new Verdict(clauseId, false, $"remote error {response.Error.Code}: {response.Error.Message}");
        var task = response.Result?.Task;
        if (task is null)
            return new Verdict(clauseId, false, "remote answer had no task");
        var text = string.Join(' ', task.Messages.SelectMany(m => m.Parts).Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t)));
        return new Verdict(clauseId, task.State == TaskState.Completed, text);
    }
}

/// <summary>Bulk summaries queued on the batch API: submit, status, results and cancel.</summary>
internal sealed class SummaryQueue(OpenAIBatchSubmitter submitter, HttpClient http, string model, TimeProvider clock) : IDisposable
{
    public async Task<BatchHandle> SubmitAsync(IReadOnlyList<LoadedFile> files, CancellationToken cancellationToken)
    {
        var requests = files
            .Select(f => new BatchRequest(
                f.Name,
                [
                    new ChatMessage(ChatRole.System, "Summarize this supplier document in two sentences."),
                    new ChatMessage(ChatRole.User, f.Text),
                ]))
            .ToList();
        return await submitter.SubmitAsync(requests, new ChatOptions { ModelId = model }, cancellationToken);
    }

    /// <summary>Polls until the batch leaves the in-progress state. Each wait goes through the clock the queue was given.</summary>
    public async Task<BatchStatus> WaitAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < ReviewDesk.MaxPolls; poll++)
        {
            var status = await submitter.GetStatusAsync(handle, cancellationToken);
            if (status.State != BatchState.InProgress)
                return status;
            await Task.Delay(ReviewDesk.PollInterval, clock, cancellationToken);
        }
        throw new TimeoutException($"Batch {handle.Id} was still in progress after the polling limit.");
    }

    public async Task<IReadOnlyList<BatchResultItem>> ResultsAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        var items = new List<BatchResultItem>();
        await foreach (var item in submitter.StreamResultsAsync(handle, cancellationToken))
            items.Add(item);
        return items;
    }

    public async Task<BatchStatus> CancelAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        await submitter.CancelAsync(handle, cancellationToken);
        return await submitter.GetStatusAsync(handle, cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
