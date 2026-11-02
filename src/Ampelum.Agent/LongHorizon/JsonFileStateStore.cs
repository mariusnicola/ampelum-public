using System.IO;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>
/// File-based persistent implementation of <see cref="IStateStore"/>.
/// Stores checkpoints as JSON files in ~/.ampelum/checkpoints/ {workflowId}/.
/// </summary>
public sealed class JsonFileStateStore : IStateStore
{
    private readonly string _baseDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonFileStateStore(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ampelum",
            "checkpoints");

        Directory.CreateDirectory(_baseDirectory);
    }

    /// <summary>Get the checkpoints directory for a workflow.</summary>
    private string GetWorkflowDirectory(string workflowId)
        => Path.Combine(_baseDirectory, ReplaceInvalidChars(workflowId));

    /// <summary>Get the JSON file path for a checkpoint.</summary>
    private string GetCheckpointPath(string workflowId, string phase, string? workspaceId)
    {
        var wfDir = GetWorkflowDirectory(workflowId);
        var wsPart = string.IsNullOrEmpty(workspaceId) ? "null" : workspaceId;
        return Path.Combine(wfDir, $"{wsPart}_{phase}.json");
    }

    /// <summary>Replace characters invalid in file names.</summary>
    private static string ReplaceInvalidChars(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    /// <summary>Deserialize JSON to PhaseCheckpoint.</summary>
    private static PhaseCheckpoint ParseCheckpoint(string json)
    {
        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
            ?? throw new JsonException("Checkpoint JSON is null");

        var completedPhases = new List<PhaseSummary>();
        if (obj.TryGetValue("completedPhases", out var cp) && cp.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in cp.EnumerateArray())
            {
                var ps = JsonSerializer.Deserialize<PhaseSummary>(elem.GetRawText(), JsonOptions)
                    ?? throw new JsonException("Failed to parse PhaseSummary");
                completedPhases.Add(ps);
            }
        }

        var res = new PhaseCheckpoint
        {
            CheckpointId = obj.TryGetValue("checkpointId", out var ci) ? ci.GetString() : null,
            WorkflowId = obj.TryGetValue("workflowId", out var w) ? w.GetString() ?? "" : "",
            AgentInstanceId = obj.TryGetValue("agentInstanceId", out var ai) ? ai.GetString() : null,
            Phase = obj.TryGetValue("phase", out var p) ? p.GetString() ?? "" : "",
            PhaseIndex = obj.TryGetValue("phaseIndex", out var pi) && pi.TryGetInt32(out var idx) ? idx : 0,
            GoalAnchor = obj.TryGetValue("goalAnchor", out var g) ? g.GetString() ?? "" : "",
            CompletedPhases = completedPhases,
            ActiveContext = obj.TryGetValue("activeContext", out var a) ? a.GetString() : null,
            CapturedAt = obj.TryGetValue("capturedAt", out var c)
                && DateTimeOffset.TryParse(c.GetString() ?? "", out var dt)
                ? dt
                : DateTimeOffset.UtcNow,
            ResumedFrom = obj.TryGetValue("resumedFrom", out var r) ? r.GetString() : null,
            PreviousCheckpointId = obj.TryGetValue("previousCheckpointId", out var pci) ? pci.GetString() : null,
            WorkspaceId = obj.TryGetValue("workspaceId", out var w2) ? w2.GetString() : null,
        };
        return res;
    }

    /// <summary>Serialize PhaseCheckpoint to JSON.</summary>
    private static string SerializeCheckpoint(PhaseCheckpoint cp)
    {
        var json = JsonSerializer.Serialize(cp, JsonOptions);
        return json;
    }

    public Task SaveAsync(PhaseCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var directory = GetWorkflowDirectory(checkpoint.WorkflowId);
        Directory.CreateDirectory(directory);

        var path = GetCheckpointPath(checkpoint.WorkflowId, checkpoint.Phase, checkpoint.WorkspaceId);
        var json = SerializeCheckpoint(checkpoint);

        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task<PhaseCheckpoint?> LoadAsync(string workflowId, CancellationToken ct = default)
        => LoadAsync(workflowId, null, ct);

    public async Task<PhaseCheckpoint?> LoadAsync(
        string workflowId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        var dir = GetWorkflowDirectory(workflowId);
        if (!Directory.Exists(dir))
            return null;

        var pattern = workspaceId == null ? "*.json" : $"{workspaceId}*.json";

        var checkpoints = new List<PhaseCheckpoint>();
        foreach (var file in Directory.EnumerateFiles(dir, pattern))
        {
            try
            {
                var text = await File.ReadAllTextAsync(file, ct);
                checkpoints.Add(ParseCheckpoint(text));
            }
            catch
            {
                continue;
            }
        }

        if (checkpoints.Count == 0)
            return null;

        return checkpoints
            .OrderByDescending(cp => cp.CapturedAt)
            .First();
    }

    public async Task<PhaseCheckpoint?> LoadByIdAsync(string checkpointId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpointId);

        // Scan all workflow directories for a checkpoint with matching CheckpointId
        if (!Directory.Exists(_baseDirectory))
            return null;

        var workflowDirs = Directory.EnumerateDirectories(_baseDirectory);
        foreach (var workflowDir in workflowDirs)
        {
            var files = Directory.EnumerateFiles(workflowDir, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    var checkpoint = ParseCheckpoint(text);
                    if (checkpoint.CheckpointId == checkpointId)
                        return checkpoint;
                }
                catch
                {
                    continue;
                }
            }
        }

        return null;
    }

    public Task<IReadOnlyList<PhaseCheckpoint>> ListAsync(string workflowId, CancellationToken ct = default)
    {
        var dir = GetWorkflowDirectory(workflowId);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<PhaseCheckpoint>>(Array.Empty<PhaseCheckpoint>());

        var results = new List<PhaseCheckpoint>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var cp = ParseCheckpoint(text);
                results.Add(cp);
            }
            catch { continue; }
        }

        return Task.FromResult<IReadOnlyList<PhaseCheckpoint>>(
            results
                .OrderByDescending(cp => cp.CapturedAt)
                .ToList()
                .AsReadOnly());
    }

    public Task PurgeAsync(string workflowId, CancellationToken ct = default)
    {
        var dir = GetWorkflowDirectory(workflowId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }
}
