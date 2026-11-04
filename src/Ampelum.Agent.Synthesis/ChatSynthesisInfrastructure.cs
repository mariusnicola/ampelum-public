using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// <see cref="ISynthesisInfrastructure"/> on an <see cref="IChatClient"/>: one model round trip per attempt with
/// validate-and-retry (the rejection reasons go back to the model), versioned files with the previous one moved
/// to a <c>.history</c> folder, and a metrics sidecar next to each file.
/// </summary>
public sealed class ChatSynthesisInfrastructure(IChatClient chat, int maxAttempts = 3) : ISynthesisInfrastructure
{
    const string HistoryFolder = ".history";
    const string MetricsSuffix = ".metrics.json";

    public async Task<SynthesisResult> GenerateAndValidateAsync(
        string prompt,
        string? existingContent,
        Func<string, string[]> validator,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var content = "";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var asked = errors.Count == 0
                ? prompt
                : $"{prompt}\n\nThe previous answer was rejected:\n- {string.Join("\n- ", errors)}\nAnswer again.";
            ChatMessage[] messages = [new ChatMessage(ChatRole.User, asked)];
            // With an ambient context the call runs that context's hooks (budget, audit, PII); without one there is no pipeline.
            var ambient = AmpelumContext.Ambient;
            var response = ambient is null
                ? await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false)
                : await HookedLmCall.InvokeAsync(ambient with { Chat = chat }, "ChatSynthesisInfrastructure.Generate", messages, cancellationToken: ct).ConfigureAwait(false);
            content = StripFences(response.Text);
            errors = [.. validator(content)];
            if (errors.Count == 0)
                return new SynthesisResult(content, true, attempt, []);
        }
        return new SynthesisResult(content, false, maxAttempts, errors);
    }

    public Task<string> WriteVersionedAsync(string canonicalPath, string content, string version, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(canonicalPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(canonicalPath))
        {
            var history = Path.Combine(directory, HistoryFolder);
            Directory.CreateDirectory(history);
            File.Move(canonicalPath, Path.Combine(history, $"{Path.GetFileNameWithoutExtension(canonicalPath)}-{version}{Path.GetExtension(canonicalPath)}"), overwrite: true);
        }
        File.WriteAllText(canonicalPath, content);
        return Task.FromResult(canonicalPath);
    }

    public async Task<WorkflowMetrics?> ReadMetricsAsync(string canonicalPath, CancellationToken ct = default)
    {
        var path = canonicalPath + MetricsSuffix;
        return File.Exists(path)
            ? JsonSerializer.Deserialize<WorkflowMetrics>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
            : null;
    }

    public async Task WriteMetricsAsync(string canonicalPath, WorkflowMetrics metrics, CancellationToken ct = default)
    {
        // Temp file then move, so a reader never sees half a file.
        var path = canonicalPath + MetricsSuffix;
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(metrics), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    static string StripFences(string text)
    {
        var lines = text.Trim().Split('\n').ToList();
        if (lines.Count > 1 && lines[0].StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
            if (lines[^1].TrimEnd() == "```")
                lines.RemoveAt(lines.Count - 1);
        }
        return string.Join('\n', lines);
    }
}
