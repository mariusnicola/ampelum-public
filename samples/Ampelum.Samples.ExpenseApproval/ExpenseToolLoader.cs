using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Fixed catalogue of the tools this sample registers with the dispatcher.</summary>
internal sealed class ExpenseToolLoader(IEnumerable<ToolManifest> manifests) : IToolLoader
{
    readonly Dictionary<string, ToolManifest> _manifests = manifests.ToDictionary(m => m.Name, StringComparer.Ordinal);

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolSurvey>>(_manifests.Values
            .Select(m => new ToolSurvey { Name = m.Name, Description = m.Description, DangerTier = m.DangerTier })
            .ToList());

    public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(_manifests.TryGetValue(toolName, out var manifest)
            ? manifest
            : throw new KeyNotFoundException($"Tool '{toolName}' not found."));

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(_manifests.GetValueOrDefault(toolName));

    public ValueTask EvictAsync(string toolName) => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default) =>
        (await SurveyAsync(ct).ConfigureAwait(false))
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
