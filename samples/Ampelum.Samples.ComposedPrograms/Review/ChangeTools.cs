using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents.Tools;
using SignalGrid.Ampelum.Tools;

// Tool inputs here are identifiers from our own files, not free text a user typed.
[assembly: AllowFreeForm]

namespace Ampelum.Samples.ComposedPrograms.Review;

public sealed record ChangeRecord(string Id, string Area, string Title, string Details);

/// <summary>One lookup of a change by id. The tool contract fixes the argument at construction.</summary>
public sealed class ChangeLookup(IReadOnlyDictionary<string, ChangeRow> changes, [FreeForm] string id) : ITypedTool<ChangeRecord?>
{
    public string Name => "change_lookup";

    public string Description => "Reads one change of the release by its id.";

    public ValueTask<ChangeRecord?> InvokeAsync(CancellationToken ct) =>
        ValueTask.FromResult(changes.TryGetValue(id.Trim(), out var row)
            ? new ChangeRecord(row.Id, row.Area, row.Title, row.Details)
            : null);
}

/// <summary>The lookup as a ReAct tool: the model sends the id as JSON, the answer goes back as JSON.</summary>
public sealed class ChangeDetailsTool(IReadOnlyDictionary<string, ChangeRow> changes) : IReActTool
{
    public string Name => "change_details";

    public string Description => "Returns the record of one change: area, title and details. Argument: id.";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"id":{"type":"string","description":"Change id such as C-101."}},"required":["id"]}""";

    public async ValueTask<string> InvokeAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        var id = arguments.RootElement.GetProperty("id").GetString() ?? "";
        var record = await new ChangeLookup(changes, id).InvokeAsync(cancellationToken).ConfigureAwait(false);
        return record is null ? $"No change with id {id}." : JsonSerializer.Serialize(record);
    }
}

/// <summary>Counts the changes of each area; the review panel reads it from the tool registry.</summary>
public sealed class ChangeCountsTool(IReadOnlyCollection<ChangeRow> changes) : IAgentTool
{
    public const string ToolName = "change_counts";
    public const string ToolVersion = "1";

    public string Name => ToolName;

    public string Description => "Counts the changes of the release per area.";

    public string Category => "release";

    public string Version => ToolVersion;

    public ValueTask<AgentToolResult> InvokeAsync(CancellationToken ct)
    {
        var counts = changes.GroupBy(c => c.Area).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        return ValueTask.FromResult(new AgentToolResult(true, string.Join(", ", counts), null, new Dictionary<string, string>()));
    }
}
