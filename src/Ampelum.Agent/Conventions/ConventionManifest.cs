using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Model for .ampelum-conventions.yaml. Deserialised from YAML,
/// merged with preset defaults, then evaluated by DefaultConventionEnforcer.</summary>
public sealed record ConventionManifest
{
    public int Version { get; init; } = 1;
    public string? Preset { get; init; }
    public ConventionOverrides? Overrides { get; init; }
    public IReadOnlyList<ExtraRule>? ExtraRules { get; init; }
    public string? ConventionsRootFile { get; init; }
}

public sealed record ConventionOverrides
{
    public int? MaxFileLines { get; init; }
    public int? MaxPublicMethodsPerClass { get; init; }
}

public sealed record ExtraRule
{
    public required string Rule { get; init; }
    public string Severity { get; init; } = "warning";
    public IReadOnlyList<LayerRule>? Layers { get; init; }
    public string? Path { get; init; }
    public string? Description { get; init; }
}

public sealed record LayerRule
{
    public required string Name { get; init; }
    public IReadOnlyList<string>? CanUse { get; init; }
}

/// <summary>Result of a convention check on a single file.</summary>
public sealed record ConventionViolation(
    string Rule,
    string Severity,
    string File,
    int? Line,
    string Message,
    string? Suggestion
);

/// <summary>Interface for convention enforcement — check files before write.</summary>
public interface IConventionEnforcer
{
    Task<IReadOnlyList<ConventionViolation>> CheckAsync(
        string filePath, string content, CancellationToken ct = default);

    Task<IReadOnlyList<ConventionViolation>> CheckAllAsync(
        IProjectPortfolio portfolio, CancellationToken ct = default);
    ConventionManifest CurrentManifest { get; }
    string? ManifestSourcePath { get; }
}

/// <summary>Known built-in convention presets. Extend by adding new static members.</summary>
public static class KnownPresets
{
    public static ConventionManifest Ampelum => new()
    {
        Version = 1,
        Overrides = new ConventionOverrides
        {
            MaxFileLines = 400,
            MaxPublicMethodsPerClass = 10,
        },
        ExtraRules = new List<ExtraRule>
        {
            new() { Rule = "no-dynamic", Severity = "warning" },
            new() { Rule = "no-bare-llm-prompts", Severity = "error" },
        },
    };

    public static ConventionManifest CleanArch => new()
    {
        Version = 1,
        Overrides = new ConventionOverrides
        {
            MaxFileLines = 500,
            MaxPublicMethodsPerClass = 15,
        },
        ExtraRules = new List<ExtraRule>
        {
            new() { Rule = "no-dynamic", Severity = "error" },
            new() { Rule = "mandatory-layer", Severity = "warning",
                Layers = new List<LayerRule>
                {
                    new() { Name = "Controllers", CanUse = new[] { "Services", "Domain" } },
                    new() { Name = "Services", CanUse = new[] { "Domain" } },
                    new() { Name = "Domain", CanUse = Array.Empty<string>() },
                }
            },
        },
    };

    public static ConventionManifest Minimal => new()
    {
        Version = 1,
        Overrides = new ConventionOverrides
        {
            MaxFileLines = 400,
        },
    };

    public static ConventionManifest Resolve(ConventionManifest? loaded)
    {
        var preset = loaded?.Preset?.ToLowerInvariant() switch
        {
            "ampelum" => Ampelum,
            "clean-arch" => CleanArch,
            "minimal" => Minimal,
            null => Ampelum,
            _ => Ampelum,
        };

        // Merge overrides from loaded manifest on top of preset
        var overrides = loaded?.Overrides is not null
            ? new ConventionOverrides
            {
                MaxFileLines = loaded.Overrides.MaxFileLines ?? preset.Overrides?.MaxFileLines,
                MaxPublicMethodsPerClass = loaded.Overrides.MaxPublicMethodsPerClass ?? preset.Overrides?.MaxPublicMethodsPerClass,
            }
            : preset.Overrides;

        var rules = new List<ExtraRule>();
        if (preset.ExtraRules is not null)
            rules.AddRange(preset.ExtraRules);
        if (loaded?.ExtraRules is not null)
            rules.AddRange(loaded.ExtraRules);

        return loaded is null
            ? preset
            : loaded with
            {
                Overrides = overrides,
                ExtraRules = rules,
            };
    }
}

/// <summary>Built-in convention rule checkers. Each implements a single deterministic rule.</summary>
internal interface IConventionChecker
{
    string RuleName { get; }
    ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig);
}

internal sealed class MaxFileLinesChecker : IConventionChecker
{
    public string RuleName => "max-file-lines";
    private readonly int _maxLines;

    public MaxFileLinesChecker(int maxLines) => _maxLines = maxLines;
    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        var lines = content.Split('\n').Length;
        if (lines > _maxLines)
        {
            return new ConventionViolation(
                RuleName, "warning", filePath, null,
                $"File is {lines} lines (max: {_maxLines})",
                $"Consider refactoring into smaller files. Target: ≤{_maxLines} lines.");
        }
        return null;
    }
}

internal sealed class MaxPublicMethodsChecker : IConventionChecker
{
    public string RuleName => "max-public-methods";
    private readonly int _maxMethods;

    public MaxPublicMethodsChecker(int maxMethods) => _maxMethods = maxMethods;
    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        var matches = Regex.Matches(content, @"public\s+\w+\s+\w+\s*\(");
        if (matches.Count > _maxMethods)
        {
            return new ConventionViolation(
                RuleName, "warning", filePath, null,
                $"File has {matches.Count} public method signatures (max: {_maxMethods})",
                $"Consider reducing public surface or splitting into multiple files.");
        }
        return null;
    }
}

internal sealed class NoDynamicChecker : IConventionChecker
{
    public string RuleName => "no-dynamic";
    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        var matches = Regex.Matches(content, @"\bdynamic\b");
        if (matches.Count > 0)
        {
            var firstLine = GetLineNumber(content, matches[0].Index);
            return new ConventionViolation(
                RuleName, ruleConfig?.Severity ?? "warning", filePath, firstLine,
                $"File contains 'dynamic' keyword ({matches.Count} occurrence(s))",
                "Replace 'dynamic' with a concrete type, 'object', or a discriminated union.");
        }
        return null;
    }

    private static int GetLineNumber(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n') line++;
        }
        return line;
    }
}

internal sealed class NoBareLlmPromptsChecker : IConventionChecker
{
    public string RuleName => "no-bare-llm-prompts";

    private static readonly Regex[] _patterns =
    {
        new(@"new\s+Predict\s*(<|\()", RegexOptions.Compiled),
        new(@"new\s+ChatMessage\s*\(", RegexOptions.Compiled),
        new(@"new\s+ChatRequest\s*\(", RegexOptions.Compiled),
        new(@"\.GetResponseAsync\s*\(", RegexOptions.Compiled),
    };

    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        var lineNumbers = new List<int>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var pattern in _patterns)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    lineNumbers.Add(i + 1);
                    break;
                }
            }
        }

        if (lineNumbers.Count > 0)
        {
            var details = string.Join(", ", lineNumbers.Take(5));
            var suffix = lineNumbers.Count > 5 ? $" and {lineNumbers.Count - 5} more" : "";
            return new ConventionViolation(
                RuleName, ruleConfig?.Severity ?? "error", filePath, lineNumbers[0],
                $"File contains bare LLM calls (lines {details}{suffix})",
                "Use the Agent's recommended LLM abstraction (IChatClient, IPredict) instead of raw SDK types.");
        }
        return null;
    }
}

internal sealed class RequiredFileChecker : IConventionChecker
{
    public string RuleName => "required-file";
    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        if (ruleConfig?.Path is null) return null;
        var requiredFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filePath) ?? ".", ruleConfig.Path);
        if (!System.IO.File.Exists(requiredFile))
        {
            return new ConventionViolation(
                RuleName, ruleConfig.Severity, filePath, null,
                $"Required file '{ruleConfig.Path}' is missing. {ruleConfig.Description}",
                $"Create '{ruleConfig.Path}' at project root.");
        }
        return null;
    }
}

internal sealed class MandatoryLayerChecker : IConventionChecker
{
    public string RuleName => "mandatory-layer";
    private readonly IReadOnlyList<LayerRule> _layers;
    private readonly Dictionary<string, LayerRule> _layersByName;

    public MandatoryLayerChecker(IReadOnlyList<LayerRule> layers)
    {
        _layers = layers ?? Array.Empty<LayerRule>();
        _layersByName = _layers.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
    }

    public ConventionViolation? Check(string filePath, string content, ExtraRule? ruleConfig)
    {
        if (_layers.Count == 0) return null;

        // Extract namespace from file path: assume path like "src/LayerName/ClassName.cs"
        // Extract the directory segment that corresponds to the layer name
        var dirPath = System.IO.Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dirPath)) return null;

        // Extract the immediate parent directory name as potential layer
        var segments = dirPath.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (segments.Length == 0) return null;

        // Find the layer segment: typically the last segment before the filename
        var lastSegment = segments[^1];

        // Check if this segment matches a defined layer
        if (!_layersByName.TryGetValue(lastSegment, out var currentLayer))
        {
            // File is not in a recognized layer directory
            var validLayers = string.Join(", ", _layers.Select(l => l.Name));
            return new ConventionViolation(
                RuleName, ruleConfig?.Severity ?? "warning", filePath, null,
                $"File is not in a recognized layer directory. Valid layers: {validLayers}",
                $"Move file to one of the valid layer directories: {validLayers}");
        }

        // Scan content for dependencies on other layers
        var violations = new List<string>();
        foreach (var layer in _layers)
        {
            if (layer.CanUse is null || layer.CanUse.Count == 0)
                continue;

            // Check if current layer is in the canUse list
            if (layer.Name.Equals(lastSegment, StringComparison.OrdinalIgnoreCase))
            {
                // Current layer is using itself — check for disallowed imports
                foreach (var otherLayer in _layers)
                {
                    if (otherLayer.CanUse is null) continue;
                    if (!otherLayer.CanUse.Contains(lastSegment, StringComparer.OrdinalIgnoreCase))
                    {
                        // Check if content imports this disallowed layer
                        var importPattern = $"using.*\\.{otherLayer.Name}";
                        if (Regex.IsMatch(content, importPattern, RegexOptions.IgnoreCase))
                        {
                            violations.Add($"Layer '{lastSegment}' cannot use '{otherLayer.Name}'");
                        }
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            return new ConventionViolation(
                RuleName, ruleConfig?.Severity ?? "warning", filePath, null,
                $"File in '{lastSegment}' layer has disallowed dependencies: {string.Join("; ", violations)}",
                $"Remove imports from disallowed layers. '{lastSegment}' layer can only use: {string.Join(", ", currentLayer.CanUse ?? Array.Empty<string>())}");
        }

        return null;
    }
}

/// <summary>Default implementation of IConventionEnforcer.
/// Loads .ampelum-conventions.yaml, merges with preset, evaluates all rules.</summary>
public sealed class DefaultConventionEnforcer : IConventionEnforcer
{
    private readonly ConventionManifest _manifest;
    private readonly string? _manifestPath;
    private readonly List<IConventionChecker> _checkers = new();

    public ConventionManifest CurrentManifest => _manifest;
    public string? ManifestSourcePath => _manifestPath;

    public DefaultConventionEnforcer(string? workspaceRoot = null)
    {
        (_manifest, _manifestPath) = LoadConventions(workspaceRoot ?? Directory.GetCurrentDirectory());
        BuildCheckers(_manifest);
    }

    public DefaultConventionEnforcer(ConventionManifest manifest, string? manifestPath = null)
    {
        _manifest = KnownPresets.Resolve(manifest);
        _manifestPath = manifestPath;
        BuildCheckers(_manifest);
    }

    private void BuildCheckers(ConventionManifest manifest)
    {
        if (manifest.Overrides is not null)
        {
            if (manifest.Overrides.MaxFileLines.HasValue)
                _checkers.Add(new MaxFileLinesChecker(manifest.Overrides.MaxFileLines.Value));
            if (manifest.Overrides.MaxPublicMethodsPerClass.HasValue)
                _checkers.Add(new MaxPublicMethodsChecker(manifest.Overrides.MaxPublicMethodsPerClass.Value));
        }

        if (manifest.ExtraRules is not null)
        {
            foreach (var rule in manifest.ExtraRules)
            {
                switch (rule.Rule)
                {
                    case "no-dynamic":
                        _checkers.Add(new NoDynamicChecker());
                        break;
                    case "no-bare-llm-prompts":
                        _checkers.Add(new NoBareLlmPromptsChecker());
                        break;
                    case "required-file":
                        _checkers.Add(new RequiredFileChecker());
                        break;
                    case "mandatory-layer":
                        if (rule.Layers is not null && rule.Layers.Count > 0)
                            _checkers.Add(new MandatoryLayerChecker(rule.Layers));
                        break;
                }
            }
        }
    }

    /// <summary>Check a single file for convention violations.</summary>
    public Task<IReadOnlyList<ConventionViolation>> CheckAsync(
        string filePath, string content, CancellationToken ct = default)
    {
        var violations = new List<ConventionViolation>();
        ExtraRule? matchingRule = null;

        foreach (var checker in _checkers)
        {
            matchingRule = _manifest.ExtraRules?.FirstOrDefault(r => r.Rule == checker.RuleName);
            var violation = checker.Check(filePath, content, matchingRule);
            if (violation is not null)
                violations.Add(violation);
        }

        return Task.FromResult<IReadOnlyList<ConventionViolation>>(violations);
    }

    /// <summary>Check all files under the workspace root (used by ampelum check-conventions).</summary>
    public async Task<IReadOnlyList<ConventionViolation>> CheckAllAsync(
        IProjectPortfolio portfolio, CancellationToken ct = default)
    {
        var all = new List<ConventionViolation>();

        foreach (var ws in portfolio.GetAll())
        {
            var root = ws.RootPath;
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var content = await File.ReadAllTextAsync(file, ct);
                var violations = await CheckAsync(file, content, ct);
                all.AddRange(violations);
            }

            // Required-file rules
            foreach (var rule in (_manifest.ExtraRules ?? Array.Empty<ExtraRule>()).Where(r => r.Rule == "required-file"))
            {
                var checker = new RequiredFileChecker();
                var v = checker.Check(root, "", rule);
                if (v is not null) all.Add(v);
            }
        }

        return all;
    }

    // ── File loading ──────────────────────────────────────────────

    private static (ConventionManifest manifest, string? sourcePath) LoadConventions(string workspaceRoot)
    {
        // Resolution order: env var > project root > user global
        var envPath = Environment.GetEnvironmentVariable("AMPELUM_CONVENTIONS");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return (LoadFromFile(envPath), envPath);

        var projectPath = Path.Combine(workspaceRoot, ".ampelum-conventions.yaml");
        if (File.Exists(projectPath))
            return (LoadFromFile(projectPath), projectPath);

        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ampelum", "conventions.yaml");
        if (File.Exists(userPath))
            return (LoadFromFile(userPath), userPath);

        // Implicit default
        return (KnownPresets.Ampelum, null);
    }

    private static ConventionManifest LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        // Minimal YAML-like parser for the subset we need.
        // In production, use YamlDotNet. For now, parse the preset + overrides fields.
        return ParseMinimalYaml(yaml);
    }

    /// <summary>Minimal parser for .ampelum-conventions.yaml.
    /// Handles: version, preset, overrides.*, extraRules[].rule/severity.
    /// Full YAML support requires YamlDotNet package.</summary>
    internal static ConventionManifest ParseMinimalYaml(string yaml)
    {
        var lines = yaml.Split('\n');
        string? preset = null;
        int? maxLines = null;
        int? maxMethods = null;
        var extraRules = new List<ExtraRule>();

        var section = "";
        ExtraRule? currentRule = null;
        List<LayerRule>? currentLayers = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            if (line == "extraRules:")
            {
                section = "extra";
                continue;
            }
            if (line == "overrides:")
            {
                section = "overrides";
                continue;
            }
            if (line.StartsWith("- rule:"))
            {
                if (currentRule is not null)
                {
                    if (currentLayers is not null)
                        currentRule = currentRule with { Layers = currentLayers };
                    extraRules.Add(currentRule);
                }
                currentRule = new ExtraRule { Rule = line[7..].Trim() };
                currentLayers = null;
                section = "rule";
                continue;
            }
            if (line.StartsWith("- {") && currentRule is not null && section != "layers")
            {
                // Skip line-based YAML array items that use inline mapping
                // Handled by the dedicated handlers below
                continue;
            }

            switch (section)
            {
                case "":
                    if (line.StartsWith("preset:")) preset = line[7..].Trim();
                    break;

                case "overrides":
                    if (line.StartsWith("maxFileLines:"))
                        maxLines = int.Parse(line[13..].Trim());
                    else if (line.StartsWith("maxPublicMethodsPerClass:"))
                        maxMethods = int.Parse(line[25..].Trim());
                    break;

                case "rule":
                    if (currentRule is null) break;
                    if (line.StartsWith("severity:"))
                        currentRule = currentRule with { Severity = line[9..].Trim() };
                    else if (line.StartsWith("path:"))
                        currentRule = currentRule with { Path = line[5..].Trim() };
                    else if (line.StartsWith("description:"))
                        currentRule = currentRule with { Description = line[12..].Trim() };
                    else if (line.StartsWith("layers:"))
                    {
                        currentLayers = new List<LayerRule>();
                        section = "layers";
                    }
                    break;

                case "layers":
                    if (line.StartsWith("- {") && line.Contains('}'))
                    {
                        // Parse inline: - { name: Controllers, canUse: [Services, Domain] }
                        var nameMatch = Regex.Match(line, @"name:\s*(\w+)");
                        var canUseMatch = Regex.Match(line, @"canUse:\s*\[([^\]]*)\]");
                        if (nameMatch.Success)
                        {
                            var canUse = canUseMatch.Success
                                ? canUseMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                : Array.Empty<string>();
                            currentLayers!.Add(new LayerRule { Name = nameMatch.Groups[1].Value, CanUse = canUse });
                        }
                    }
                    else if (!line.StartsWith("-")) // back to parent level
                        section = "rule";
                    break;
            }
        }

        if (currentRule is not null)
        {
            if (currentLayers is not null)
                currentRule = currentRule with { Layers = currentLayers };
            extraRules.Add(currentRule);
        }

        return new ConventionManifest
        {
            Version = 1,
            Preset = preset,
            Overrides = new ConventionOverrides
            {
                MaxFileLines = maxLines,
                MaxPublicMethodsPerClass = maxMethods,
            },
            ExtraRules = extraRules.Count > 0 ? extraRules : null,
        };
    }
}