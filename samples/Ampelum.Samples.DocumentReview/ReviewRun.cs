using System.Globalization;
using SignalGrid.Ampelum.Batch;

namespace Ampelum.Samples.DocumentReview;

/// <summary>One pass over the incoming supplier documents. Returns 0 when every step did what it should.</summary>
internal static class ReviewRun
{
    /// <summary>Runs, and turns a failure the user can act on (missing setting, bad answer, polling limit) into an [ERROR] line and exit code 1.</summary>
    public static async Task<int> RunReportingErrorsAsync(
        ReviewSettings settings,
        ReviewTransport transport,
        Func<IReadOnlyList<(RerankerKind Kind, string Key, string Model)>> rerankers,
        string workDir,
        TextWriter output,
        TextWriter errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(settings, transport, rerankers, workDir, output, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TimeoutException)
        {
            errors.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> RunAsync(
        ReviewSettings settings,
        ReviewTransport transport,
        Func<IReadOnlyList<(RerankerKind Kind, string Key, string Model)>> rerankers,
        string workDir,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var desk = new ReviewDesk(settings, transport);
        var ok = true;

        var paths = await SupplierDocuments.WriteAllAsync(workDir, cancellationToken);
        var files = await desk.LoadAsync(paths, cancellationToken);
        foreach (var file in files)
        {
            if (file.Pages.Count == 0)
            {
                output.WriteLine(file.Error is null
                    ? $"Load:     {file.Name}: no pages, the PDF is empty and needs a person"
                    : $"Load:     {file.Name}: {file.Error}; it needs a person");
                continue;
            }
            output.WriteLine($"Load:     {file.Name}: {file.Pages.Count} page(s)");
        }
        var readable = files.Where(f => f.Pages.Count > 0).ToList();

        IReadOnlyList<RankedClause>? firstRanking = null;
        foreach (var (kind, key, model) in rerankers())
        {
            var ranking = await desk.RerankAsync(kind, key, model, readable, SupplierDocuments.Question, cancellationToken);
            firstRanking ??= ranking;
            foreach (var clause in ranking)
                output.WriteLine($"Rerank:   {kind} {clause.Id} score {clause.Score.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        var hard = firstRanking!.Where(c => c.Score < ReviewDesk.AutoApproveScore).ToList();
        foreach (var clause in firstRanking!.Where(c => c.Score >= ReviewDesk.AutoApproveScore))
            output.WriteLine($"Accepted: {clause.Id} (score at or above {ReviewDesk.AutoApproveScore.ToString("0.00", CultureInfo.InvariantCulture)})");
        if (hard.Count > 0)
        {
            var (agentName, verdicts) = await desk.EscalateAsync(hard, SupplierDocuments.Question, cancellationToken);
            output.WriteLine($"Agent:    {agentName}");
            foreach (var verdict in verdicts)
            {
                output.WriteLine($"Verdict:  {verdict.ClauseId}: {(verdict.Completed ? "completed" : "FAILED")}: {verdict.Text}");
                ok &= verdict.Completed;
            }
        }

        using var queue = desk.OpenQueue();
        var overnight = readable.Where(f => SupplierDocuments.Overnight.Contains(f.Name)).ToList();
        var handle = await queue.SubmitAsync(overnight, cancellationToken);
        output.WriteLine($"Batch:    {handle.Id} submitted with {overnight.Count} document(s)");
        var status = await queue.WaitAsync(handle, cancellationToken);
        output.WriteLine($"Status:   {status.State}, {status.CompletedCount} of {status.TotalCount} done, {status.FailedCount} failed");
        var results = status.State == BatchState.Completed ? await queue.ResultsAsync(handle, cancellationToken) : [];
        foreach (var item in results)
        {
            output.WriteLine($"Summary:  {item.CustomId}: {item.Response?.Text ?? "ERROR " + item.ErrorMessage}");
            ok &= item.Response is not null;
        }
        ok &= status.State == BatchState.Completed && results.Count == overnight.Count;

        var withdrawn = readable.Where(f => f.Name == SupplierDocuments.Withdrawn).ToList();
        var late = await queue.SubmitAsync(withdrawn, cancellationToken);
        output.WriteLine($"Batch:    {late.Id} submitted with {withdrawn.Count} document(s)");
        var cancelled = await queue.CancelAsync(late, cancellationToken);
        output.WriteLine($"Cancel:   {late.Id} is now {cancelled.State}");
        ok &= cancelled.State == BatchState.Cancelled;

        return ok ? 0 : 1;
    }
}
