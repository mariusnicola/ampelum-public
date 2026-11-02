using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Portfolio registry that persists workspace metadata to a JSON file.
/// Service instances (TokenStore, SecretStore, Skills) are always reconstructed fresh —
/// they are session-local and never serialized.</summary>
public sealed class JsonFileProjectRegistry : IProjectPortfolio
{
    private readonly string _filePath;
    private readonly InMemoryProjectRegistry _inner = new();

    private static readonly JsonSerializerOptions _opts = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonFileProjectRegistry(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ampelum", "portfolio.json");
        Load();
    }

    public void Register(WorkspaceContext workspace)
    {
        _inner.Register(workspace);
        Save();
    }

    public async ValueTask DeregisterAsync(WorkspaceId id, CancellationToken ct = default)
    {
        await _inner.DeregisterAsync(id, ct);
        Save();
    }

    public WorkspaceContext? Get(WorkspaceId id) => _inner.Get(id);
    public IReadOnlyList<WorkspaceContext> GetAll() => _inner.GetAll();
    public bool IsEmpty => _inner.IsEmpty;

    // ── Persistence ───────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var dto = JsonSerializer.Deserialize<PortfolioDto>(json, _opts);
            if (dto?.Workspaces is null) return;
            foreach (var w in dto.Workspaces)
                _inner.Register(Reconstruct(w));
        }
        catch (Exception) { /* corrupt — start empty */ }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var dto = new PortfolioDto
        {
            Workspaces = _inner.GetAll().Select(ToDto).ToList(),
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, _opts));
    }

    private static WorkspaceDto ToDto(WorkspaceContext ws) => new()
    {
        Id = ws.Id.Value,
        Label = ws.Label,
        RootPath = ws.RootPath,
        PriorityWeight = ws.PriorityWeight,
        EscalationMode = ws.EscalationPolicy.Mode,
        MaxAutoApproveTier = ws.EscalationPolicy.MaxAutoApproveTier,
        BlockList = ws.EscalationPolicy.BlockList.ToList(),
    };

    private static WorkspaceContext Reconstruct(WorkspaceDto dto)
    {
        var policy = dto.EscalationMode switch
        {
            EscalationMode.AutoApprove => EscalationPolicy.AutoApprove(
                dto.MaxAutoApproveTier,
                dto.BlockList.Count > 0 ? new HashSet<string>(dto.BlockList) : null),
            EscalationMode.Queue => EscalationPolicy.Queue(),
            EscalationMode.Skip => EscalationPolicy.Skip(),
            _ => EscalationPolicy.AbortTask(),
        };

        return new WorkspaceContext
        {
            Id = new WorkspaceId(dto.Id),
            Label = dto.Label,
            RootPath = dto.RootPath,
            PriorityWeight = dto.PriorityWeight,
            EscalationPolicy = policy,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
    }

    // ── DTOs ──────────────────────────────────────────────────────

    private sealed class PortfolioDto
    {
        public List<WorkspaceDto> Workspaces { get; set; } = new();
    }

    private sealed class WorkspaceDto
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public int PriorityWeight { get; set; } = 50;
        public EscalationMode EscalationMode { get; set; } = EscalationMode.AutoApprove;
        public DangerTier MaxAutoApproveTier { get; set; } = DangerTier.Destructive;
        public List<string> BlockList { get; set; } = new();
    }
}