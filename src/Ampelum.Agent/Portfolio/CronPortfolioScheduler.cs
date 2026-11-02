using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Cron-style scheduler for portfolio runs.
/// Persists schedules to a JSON file and triggers IPortfolioOrchestrator when due.
/// Report sanitization is injected as a delegate so Ampelum.Agent keeps its
/// zero-dependency-on-Ampelum invariant.</summary>
public sealed class CronPortfolioScheduler : IPortfolioScheduler
{
    private readonly IPortfolioOrchestrator _orchestrator;
    private readonly Func<string, string>? _sanitizer;
    private readonly string _schedulesPath;
    private readonly List<ScheduleEntry> _entries = new();

    private static readonly JsonSerializerOptions _opts = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public CronPortfolioScheduler(
        IPortfolioOrchestrator orchestrator,
        Func<string, string>? sanitizer = null,
        string? schedulesPath = null)
    {
        _orchestrator = orchestrator;
        _sanitizer = sanitizer;
        _schedulesPath = schedulesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ampelum", "schedules.json");
        Load();
    }

    public ScheduleId Schedule(string intent, PortfolioOptions options, ScheduleExpression expression)
    {
        var id = new ScheduleId($"sched-{Guid.NewGuid():N}");
        var entry = new ScheduleEntry
        {
            Id = id,
            Intent = intent,
            Options = options,
            Expression = expression,
        };
        _entries.Add(entry);
        Save();
        return id;
    }

    public void Remove(ScheduleId id)
    {
        _entries.RemoveAll(e => e.Id == id);
        Save();
    }

    public IReadOnlyList<ScheduleEntry> List() => _entries.AsReadOnly();

    public async Task RunPendingAsync(IProjectPortfolio portfolio, DateTimeOffset? now = null)
    {
        var nowUtc = now ?? DateTimeOffset.UtcNow;
        var due = _entries.Where(e => IsDue(e, nowUtc)).ToList();

        foreach (var entry in due)
        {
            var result = await _orchestrator.RunAsync(entry.Intent, entry.Options);

            foreach (var ws in portfolio.GetAll())
            {
                var report = BuildReport(entry, result, ws);
                var safeReport = _sanitizer?.Invoke(report) ?? report;
                WriteReport(ws, safeReport);
            }

            // Update last-run timestamp and remove one-shot schedules
            var idx = _entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0)
            {
                _entries[idx] = entry with { LastRunAt = nowUtc };
                if (entry.Expression.Repeat == RepeatInterval.Once)
                    _entries.RemoveAt(idx);
            }
        }

        if (due.Count > 0)
            Save();
    }

    private static bool IsDue(ScheduleEntry entry, DateTimeOffset now)
    {
        var todayAtTime = new DateTimeOffset(
            now.Year, now.Month, now.Day,
            entry.Expression.TimeOfDay.Hour,
            entry.Expression.TimeOfDay.Minute,
            entry.Expression.TimeOfDay.Second,
            now.Offset);

        if (entry.LastRunAt is null)
            return now >= todayAtTime;

        // Already ran today? Skip.
        return entry.LastRunAt.Value.Date < now.Date && now >= todayAtTime;
    }

    private static string BuildReport(ScheduleEntry entry, PortfolioResult result, WorkspaceContext ws)
    {
        var wsResult = result.CompletedWorkspaces.FirstOrDefault(r => r.WorkspaceId == ws.Id);
        var status = wsResult?.Success == true ? "OK" : "FAILED";
        var error = wsResult?.Error;

        return $"""
            Nightly Report — {ws.Label}
            ========================================
            Schedule: {entry.Id.Value}
            Intent:   {entry.Intent}
            Status:   {status}
            Completed: {result.CompletedAt:u}
            Tasks:     {(wsResult?.TaskResults.Count ?? 0)}
            {(!string.IsNullOrEmpty(error) ? $"Error: {error}" : "")}
            Escalations: {result.PendingEscalations.Count}
            """;
    }

    private void WriteReport(WorkspaceContext ws, string content)
    {
        if (string.IsNullOrEmpty(ws.RootPath)) return;
        var path = Path.Combine(ws.RootPath, "NIGHTLY_REPORT.md");
        File.WriteAllText(path, content);
    }

    // ── Persistence ───────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_schedulesPath)) return;
        try
        {
            var json = File.ReadAllText(_schedulesPath);
            var dto = JsonSerializer.Deserialize<SchedulesDto>(json, _opts);
            if (dto?.Entries is null) return;
            _entries.AddRange(dto.Entries.Select(Reconstruct));
        }
        catch (Exception) { /* corrupt — start empty */ }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_schedulesPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var dto = new SchedulesDto
        {
            Entries = _entries.Select(ToDto).ToList(),
        };
        File.WriteAllText(_schedulesPath, JsonSerializer.Serialize(dto, _opts));
    }

    private static EntryDto ToDto(ScheduleEntry entry) => new()
    {
        Id = entry.Id.Value,
        Intent = entry.Intent,
        TimeOfDay = entry.Expression.TimeOfDay.ToString("HH:mm"),
        Repeat = entry.Expression.Repeat,
        LastRunAt = entry.LastRunAt?.ToString("O"),
    };

    private static ScheduleEntry Reconstruct(EntryDto dto)
    {
        var parts = dto.TimeOfDay.Split(':');
        var hour = int.Parse(parts[0]);
        var minute = parts.Length > 1 ? int.Parse(parts[1]) : 0;

        DateTimeOffset? lastRun = null;
        if (!string.IsNullOrEmpty(dto.LastRunAt))
            lastRun = DateTimeOffset.Parse(dto.LastRunAt);

        return new ScheduleEntry
        {
            Id = new ScheduleId(dto.Id),
            Intent = dto.Intent,
            Options = PortfolioOptions.Default,
            Expression = new ScheduleExpression
            {
                TimeOfDay = new TimeOnly(hour, minute),
                Repeat = dto.Repeat,
            },
            LastRunAt = lastRun,
        };
    }

    // ── DTOs ──────────────────────────────────────────────────────

    private sealed class SchedulesDto
    {
        public List<EntryDto> Entries { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string Id { get; set; } = string.Empty;
        public string Intent { get; set; } = string.Empty;
        public string TimeOfDay { get; set; } = "02:00";
        public RepeatInterval Repeat { get; set; } = RepeatInterval.Daily;
        public string? LastRunAt { get; set; }
    }
}