using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Default implementation of <see cref="IToolLoader"/>.
/// Loads tool manifests from registered <see cref="IToolSource"/> instances.
/// Key features:
/// - Schema injection: automatically injects <c>observationToken</c> parameter into JSON Schema
///   for YAML tools with <c>requiresObservationToken</c>
/// - Safe default: applies <c>requiresObservationToken</c> for Mutating+ tools without
///   explicit requirement or exemption
/// - Exemption validation: validates controlled vocabulary and required fields
/// - Manifest caching: caches after first <c>TryUnpackAsync</c> call
/// - LRU cap: keeps max 30 tools in survey, most recently used kept
/// - Hot-reload: FileSystemWatcher invalidates cache on file changes
/// </summary>
public sealed class ToolLoader : IToolLoader, IDisposable
{
    private readonly IEnumerable<IToolSource> _sources;
    private readonly Dictionary<string, ToolManifest> _cache = new(StringComparer.Ordinal);
    private IReadOnlyList<ToolSurvey>? _surveyCache;
    private readonly LinkedList<string> _lruOrder = new(); // Track access order
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Creates a new ToolLoader from the given sources.</summary>
    public ToolLoader(IEnumerable<IToolSource> sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        SetupFileWatchers();
    }

    /// <summary>Set up FileSystemWatcher for file-based tool sources.</summary>
    private void SetupFileWatchers()
    {
        foreach (var source in _sources)
        {
            if (source is not IFileToolSource fileSource)
                continue;

            try
            {
                var watcher = new FileSystemWatcher(fileSource.SourceDirectory)
                {
                    Filter = "*.yml",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                };

                watcher.Changed += (_, e) => InvalidateTool(Path.GetFileNameWithoutExtension(e.Name ?? ""));
                watcher.Renamed += (_, e) => InvalidateTool(Path.GetFileNameWithoutExtension(e.OldName ?? ""));
                watcher.Deleted += (_, e) => InvalidateTool(Path.GetFileNameWithoutExtension(e.Name ?? ""));

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch
            {
                // Ignore watcher setup failures
            }
        }
    }

    /// <summary>Invalidate cache for a specific tool.</summary>
    private void InvalidateTool(string toolName)
    {
        lock (_lock)
        {
            _cache.Remove(toolName);
            _surveyCache = null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_surveyCache is not null)
                return _surveyCache;
        }

        var surveys = new List<ToolSurvey>();
        foreach (var source in _sources)
        {
            var sourceSurveys = await source.SurveyAsync(ct);
            foreach (var s in sourceSurveys)
            {
                surveys.Add(new ToolSurvey
                {
                    Name = $"{source.SourcePrefix}/{s.Name}",
                    Description = s.Description,
                    DangerTier = s.DangerTier,
                    RequiresConfirmation = s.RequiresConfirmation
                });
            }
        }

        // Initialize LRU list from survey order (first time only)
        lock (_lock)
        {
            if (_lruOrder.Count == 0)
            {
                foreach (var survey in surveys)
                {
                    var node = _lruOrder.AddLast(survey.Name);
                    _lruNodes[survey.Name] = node;
                }
            }

            // Cap at 30 most recently used tools
            var capped = CapTo30(surveys);
            _surveyCache = capped.AsReadOnly();
            return _surveyCache;
        }
    }

    /// <summary>Cap survey list to 30 most recently used tools.</summary>
    private List<ToolSurvey> CapTo30(List<ToolSurvey> surveys)
    {
        if (surveys.Count <= 30)
            return surveys;

        // Keep the first 30 from the LRU order
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var node = _lruOrder.First;
        int count = 0;
        while (node is not null && count < 30)
        {
            kept.Add(node.Value);
            node = node.Next;
            count++;
        }

        return surveys.Where(s => kept.Contains(s.Name)).ToList();
    }

    /// <inheritdoc />
    public async Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default)
    {
        var result = await TryUnpackAsync(toolName, ct);
        if (result is null)
            throw new KeyNotFoundException($"Tool '{toolName}' not found.");
        return result;
    }

    /// <inheritdoc />
    public async Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        // Check cache first
        lock (_lock)
        {
            if (_cache.TryGetValue(toolName, out var cached))
            {
                // Move to front of LRU list
                UpdateLRU(toolName);
                return cached;
            }
        }

        // Resolve the tool from sources
        foreach (var source in _sources)
        {
            // Check if this source owns the tool (prefix match)
            if (!toolName.StartsWith($"{source.SourcePrefix}/", StringComparison.Ordinal))
                continue;

            var localName = toolName[(source.SourcePrefix.Length + 1)..];
            var manifest = await source.TryUnpackAsync(localName, ct);
            if (manifest is null)
                continue;

            // Validate and enrich the manifest
            var enriched = ValidateAndEnrich(manifest);
            if (enriched is null)
                return null;

            // Cache and update LRU
            lock (_lock)
            {
                _cache[toolName] = enriched;
                UpdateLRU(toolName);
            }

            return enriched;
        }

        return null;
    }

    /// <summary>Update LRU position: move tool to front of the list.</summary>
    private void UpdateLRU(string toolName)
    {
        if (_lruNodes.TryGetValue(toolName, out var node))
        {
            _lruOrder.Remove(node);
            var newNode = _lruOrder.AddFirst(toolName);
            _lruNodes[toolName] = newNode;
        }
        else
        {
            var newNode = _lruOrder.AddFirst(toolName);
            _lruNodes[toolName] = newNode;
        }
    }

    /// <inheritdoc />
    public ValueTask EvictAsync(string toolName)
    {
        lock (_lock)
        {
            _cache.Remove(toolName);
            _surveyCache = null;
        }
        return default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var survey = await SurveyAsync(ct);
        var lowerQuery = query.ToLowerInvariant();

        var results = survey
            .Where(s => s.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
                     || s.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        return results.AsReadOnly();
    }

    /// <summary>Validate and enrich a tool manifest.
    /// Applies safe default, injects schema, and validates exemptions.</summary>
    private ToolManifest? ValidateAndEnrich(ToolManifest manifest)
    {
        // 1. Apply safe default for Mutating+ tools
        manifest = ApplySafeDefault(manifest);

        // 2. Validate exemptions
        if (!ValidateExemption(manifest))
            return null;

        // 3. Inject observationToken parameter into the JSON Schema (for YAML tools)
        manifest = InjectSchema(manifest);

        return manifest;
    }

    /// <summary>Apply safe default: Mutating+ tools without explicit requirement or exemption
    /// get an automatic <c>requiresObservationToken</c>.</summary>
    private static ToolManifest ApplySafeDefault(ToolManifest manifest)
    {
        // Only apply to Mutating+ tiers (Destructive, External, Catastrophic)
        if (manifest.DangerTier < DangerTier.Destructive)
            return manifest;

        // Already has explicit requirement or exemption
        if (manifest.RequiresObservationToken is not null)
            return manifest;
        if (manifest.ObservationExemption is not null)
            return manifest;

        // Apply safe default: require observation token with argName="observationToken"
        return manifest with
        {
            RequiresObservationToken = new ObservationRequirement
            {
                ArgName = "observationToken",
                AcceptedTypes = [] // empty = any type accepted
            }
        };
    }

    /// <summary>Validate the observation exemption, if present.
    /// Returns false if the exemption is invalid.</summary>
    private static bool ValidateExemption(ToolManifest manifest)
    {
        var exemption = manifest.ObservationExemption;
        if (exemption is null)
            return true; // no exemption to validate

        // Exemptions are only valid for Mutating+ tools
        if (manifest.DangerTier < DangerTier.Destructive)
        {
            // Free text / invalid exemption value
            return false;
        }

        switch (exemption)
        {
            case Agent.ObservationExemption.NewResourceCreation:
                // No additional validation needed at manifest level.
                // ToolDispatcher handles runtime validation that no arg references an existing path.
                return true;

            case Agent.ObservationExemption.AtomicReadWrite:
                // Must declare which arg performs the internal read
                if (string.IsNullOrEmpty(manifest.AtomicReadArg))
                    return false;
                return true;

            case Agent.ObservationExemption.ExternalSystemBootstrap:
                // Requires one-sentence justification
                if (string.IsNullOrEmpty(manifest.BootstrapJustification))
                    return false;
                if (manifest.BootstrapJustification.Length < 10)
                    return false; // too short to be meaningful
                return true;

            default:
                // Unknown exemption value — free text rejected
                return false;
        }
    }

    /// <summary>Inject the <c>observationToken</c> parameter into the tool's JSON Schema.
    /// The LLM sees the injected parameter in the <c>Unpack</c> result
    /// and knows it must provide a token. The requirement is self-documenting via the schema.</summary>
    private static ToolManifest InjectSchema(ToolManifest manifest)
    {
        if (manifest.RequiresObservationToken is null)
            return manifest;

        var requirement = manifest.RequiresObservationToken;
        if (manifest.ArgsSchema is null || manifest.ArgsSchema.Value.ValueKind == JsonValueKind.Undefined)
            return manifest;

        try
        {
            var schema = manifest.ArgsSchema.Value;

            // Create a mutable copy of the schema
            var schemaObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.GetRawText());
            if (schemaObj is null)
                return manifest;

            // Get or create 'properties' object
            JsonElement properties;
            bool hasProperties = schemaObj.TryGetValue("properties", out properties);

            var propsObj = hasProperties && properties.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(properties.GetRawText()) ?? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement>();

            // Inject the observationToken property if not already present
            if (!propsObj.ContainsKey(requirement.ArgName))
            {
                var tokenProperty = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["type"] = JsonSerializer.SerializeToElement("string"),
                    ["description"] = JsonSerializer.SerializeToElement(
                        $"Observation token from a prior {DescribeTokenSource(requirement)} call. " +
                        "Required to verify the resource was observed before mutation.")
                });

                propsObj[requirement.ArgName] = tokenProperty;
            }

            schemaObj["properties"] = JsonSerializer.SerializeToElement(propsObj);

            // Inject into 'required' array if it exists
            if (schemaObj.TryGetValue("required", out var requiredElem) && requiredElem.ValueKind == JsonValueKind.Array)
            {
                var requiredList = JsonSerializer.Deserialize<List<string>>(requiredElem.GetRawText()) ?? new List<string>();
                if (!requiredList.Contains(requirement.ArgName))
                {
                    requiredList.Add(requirement.ArgName);
                    schemaObj["required"] = JsonSerializer.SerializeToElement(requiredList);
                }
            }
            else
            {
                // Create a new required array
                schemaObj["required"] = JsonSerializer.SerializeToElement(new[] { requirement.ArgName });
            }

            return manifest with
            {
                ArgsSchema = JsonSerializer.SerializeToElement(schemaObj)
            };
        }
        catch (JsonException)
        {
            // If schema manipulation fails, return the original manifest
            return manifest;
        }
    }

    /// <summary>Describe what kind of observation call the token comes from, for schema descriptions.</summary>
    private static string DescribeTokenSource(ObservationRequirement requirement)
    {
        if (requirement.AcceptedTypes.Length == 0)
            return "read or list";

        return requirement.AcceptedTypes switch
        {
            [TokenType.ReadToken] => "read_file",
            [TokenType.ListToken] => "list_directory",
            _ => "read or list"
        };
    }

    /// <summary>Dispose watchers.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _disposed = true;
    }
}