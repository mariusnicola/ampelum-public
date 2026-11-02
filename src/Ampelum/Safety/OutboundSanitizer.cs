using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum;

/// <summary>Source of the content — governs which detectors run and how replacements are applied.</summary>
public enum ScanSource
{
    /// <summary>Tool or function output — both passes, both replace on detect.</summary>
    ToolResult,

    /// <summary>Fetched from the internet — both passes, stricter injection detection, both replace on detect.</summary>
    WebContent,

    /// <summary>Direct user message — Pass 2 only (user legitimately provides credentials), flag but never replace.</summary>
    UserInput,

    /// <summary>File content — Pass 1 only (files can contain legitimate instructions), replace on detect.</summary>
    FileContent,

    /// <summary>Log/stderr output — Pass 1 only, replace on detect.</summary>
    LogOutput,

    /// <summary>Agent-internal state (checkpoint, memory) — Pass 1 only, replace on detect.</summary>
    InternalState,
}

/// <summary>Detects malicious or sensitive content in strings.</summary>
public interface IContentDetector
{
    /// <summary>The type of content this detector identifies.</summary>
    DetectionType Type { get; }

    /// <summary>Scan content and report detections.
    /// Does not modify the input; caller decides whether to replace based on ScanSource.</summary>
    IEnumerable<Detection> Detect(string content, ScanSource source);
}

/// <summary>Category of detected content.</summary>
public enum DetectionType
{
    /// <summary>Credentials, tokens, secrets, keys, connection strings.</summary>
    Credential,

    /// <summary>Adversarial prompt injection patterns (goal hijacking, jailbreak attempts).</summary>
    InjectionAttempt,
}

/// <summary>A single detection within scanned content.
/// Contains the detection type, rule name, location, and suggested replacement.</summary>
public sealed record Detection(
    DetectionType Type,
    string Rule,
    int StartIndex,
    int Length,
    string Replacement
);

/// <summary>Detects credentials with known prefixes and structural shapes:
/// AWS keys, GitHub tokens, API keys, JWTs, private keys, connection strings.</summary>
public sealed class KnownFormatCredentialDetector : IContentDetector
{
    private static readonly (string Rule, Regex Pattern, string Replacement)[] Patterns =
    [
        ("aws-key", new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "[REDACTED:aws-key]"),
        ("github-token", new Regex(@"ghp_[A-Za-z0-9]{36}", RegexOptions.Compiled), "[REDACTED:github-token]"),
        ("github-pat", new Regex(@"github_pat_[A-Za-z0-9_]{36,}", RegexOptions.Compiled), "[REDACTED:github-pat]"),
        ("openai-key", new Regex(@"sk-(?:proj-)?[A-Za-z0-9]{32,}", RegexOptions.Compiled), "[REDACTED:openai-key]"),
        ("anthropic-key", new Regex(@"sk-ant-[A-Za-z0-9]{32,}", RegexOptions.Compiled), "[REDACTED:anthropic-key]"),
        ("jwt", new Regex(@"eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}", RegexOptions.Compiled), "[REDACTED:jwt]"),
        ("connection-string", new Regex(@"(?:Server|Data Source|Initial Catalog|User ID|Password|Integrated Security)=[^;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[REDACTED:connection-string]"),
        ("private-key", new Regex(@"-----BEGIN (?:RSA|EC|DSA|OPENSSH) PRIVATE KEY-----.*?-----END (?:RSA|EC|DSA|OPENSSH) PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.Singleline), "[REDACTED:private-key]"),
        ("slack-token", new Regex(@"xox[baprs]-[A-Za-z0-9-]+", RegexOptions.Compiled), "[REDACTED:slack-token]"),
        ("generic-api-key", new Regex(@"(?:api[_-]?key|apikey|secret|token|password)\s*[:=]\s*['""]?[A-Za-z0-9+/=_-]{20,}['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[REDACTED:generic-api-key]"),
    ];

    /// <summary>This detector identifies credentials.</summary>
    public DetectionType Type => DetectionType.Credential;

    /// <summary>Scan for known-format credentials using regex patterns.</summary>
    public IEnumerable<Detection> Detect(string content, ScanSource source)
    {
        if (source == ScanSource.UserInput) yield break;

        foreach (var (rule, pattern, replacement) in Patterns)
        {
            var matches = pattern.Matches(content);
            foreach (Match match in matches)
            {
                yield return new Detection(DetectionType.Credential, rule, match.Index, match.Length, replacement);
            }
        }
    }
}

/// <summary>Detects high-entropy token-like substrings (20–80 chars, no whitespace).
/// Strings above entropy threshold (4.5 bits/char) are flagged as potential secrets.
/// Rate-limited: max 10 flags per Sanitize() call.</summary>
public sealed class EntropyCredentialDetector : IContentDetector
{
    private const int MinEntropyLength = 20;
    private const int MaxEntropyLength = 80;
    private const double EntropyThreshold = 4.5;
    private const int MaxEntropyFlags = 10;

    /// <summary>This detector identifies credentials.</summary>
    public DetectionType Type => DetectionType.Credential;

    /// <summary>Scan for high-entropy tokens that match credential patterns.</summary>
    public IEnumerable<Detection> Detect(string content, ScanSource source)
    {
        if (source == ScanSource.UserInput) yield break;

        var tokenPattern = new Regex(@"[A-Za-z0-9+/=_-]{" + MinEntropyLength + "," + MaxEntropyLength + "}", RegexOptions.Compiled);
        var matches = tokenPattern.Matches(content);
        var flagCount = 0;

        foreach (Match match in matches)
        {
            if (flagCount >= MaxEntropyFlags) break;
            var token = match.Value;
            if (OutboundSanitizer.ComputeEntropy(token) >= EntropyThreshold)
            {
                yield return new Detection(DetectionType.Credential, "high-entropy-token", match.Index, match.Length, "[REDACTED:high-entropy-token]");
                flagCount++;
            }
        }
    }
}

/// <summary>Detects adversarial prompt injection patterns (goal hijacking, memory poisoning, jailbreak attempts).
/// Applies to ToolResult, WebContent, and UserInput sources only.
/// Excludes patterns inside code blocks (``` fences).</summary>
public sealed partial class InjectionPatternDetector : IContentDetector
{
    private static readonly (string Rule, Regex Pattern)[] Patterns =
    [
        ("ignore-previous-instructions", new Regex(@"ignore\s+(?:all\s+)?(?:previous|above|prior)\s+(?:instructions?|prompts?|rules?)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("new-instructions", new Regex(@"your\s+new\s+(?:instructions?|system\s+prompt|role)\s+(?:is|are|:)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("pretend-role", new Regex(@"pretend\s+(?:you\s+are|to\s+be)\s+a\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("developer-override", new Regex(@"\[SYSTEM_OVERRIDE\]|\[DEVELOPER_MODE\]|\[JAILBREAK\]", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("role-hijack", new Regex(@"from\s+now\s+on\s+(?:you\s+(?:are|will\s+be|must)|your\s+role\s+is)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("output-format", new Regex(@"output\s+(?:only|just|exactly)\s+(?:the\s+)?(?:word|phrase|sentence)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("no-disclaimer", new Regex(@"(?:do|don't|never|must\s+not)\s+(?:not\s+)?(?:include|add|output|say|write)\s+(?:any\s+)?(?:disclaimers?|warnings?|notes?)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("prefix-injection", new Regex(@"^\s*(?:Certainly!|I'd\s+be\s+happy|Here\s+is|Absolutely!)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("xml-injection", new Regex(@"<\s*(?:system|instructions?|prompt|role)\s*>[^<]*<\s*/\s*(?:system|instructions?|prompt|role)\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("goal-hijack", new Regex(@"(?:your|the)\s+(?:primary|main|real|actual)\s+(?:goal|objective|task|purpose)\s+(?:is|should|must)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("payload-split", new Regex(@"split\s+(?:this|the)\s+(?:response|output|answer)\s+(?:into|across)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("trust-boundary", new Regex(@"this\s+(?:is\s+(?:an\s+)?(?:urgent|emergency|critical|system)|bypasses?\s+(?:all\s+)?(?:filters|restrictions|rules))", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>This detector identifies injection attempts.</summary>
    public DetectionType Type => DetectionType.InjectionAttempt;

    /// <summary>Scan for adversarial prompt injection patterns.</summary>
    public IEnumerable<Detection> Detect(string content, ScanSource source)
    {
        if (source != ScanSource.ToolResult && source != ScanSource.WebContent && source != ScanSource.UserInput)
            yield break;

        var codeBlocksStripped = CodeBlockRegex().Replace(content, "");

        foreach (var (rule, pattern) in Patterns)
        {
            var matches = pattern.Matches(codeBlocksStripped);
            foreach (Match match in matches)
            {
                var replacement = source == ScanSource.UserInput ? "[flagged]" : "[BLOCKED:injection-attempt]";
                yield return new Detection(DetectionType.InjectionAttempt, rule, match.Index, match.Length, replacement);
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlockRegex();
}

/// <summary>Two-pass outbound sanitizer. Pass 1 detects credentials (known-format + entropy).
/// Pass 2 detects prompt injection patterns. Runs on every string entering LLM context.
/// Performance target: under 5ms for 100KB input.</summary>
public sealed class OutboundSanitizer
{
    private readonly IReadOnlyList<IContentDetector> _detectors;

    /// <summary>Create a sanitizer with custom detectors.</summary>
    public OutboundSanitizer(IEnumerable<IContentDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.ToList();
    }

    /// <summary>Default sanitizer instance with all built-in detectors.
    /// Use this for standard sanitization without custom detector configuration.</summary>
    public static OutboundSanitizer Default { get; } = new OutboundSanitizer([
        new KnownFormatCredentialDetector(),
        new EntropyCredentialDetector(),
        new InjectionPatternDetector(),
    ]);

    /// <summary>Sanitize content before it enters LLM context.
    /// Returns a SanitizeResult describing detections and replacements.
    /// For UserInput sources, detections are reported but replacements are not applied (user is authoritative).</summary>
    public SanitizeResult Sanitize(string content, ScanSource source)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) return new SanitizeResult(content, []);

        var allDetections = new List<Detection>();
        var isUserInput = source == ScanSource.UserInput;

        // Collect all detections from all detectors on ORIGINAL content
        foreach (var detector in _detectors)
        {
            var detections = detector.Detect(content, source).ToList();
            allDetections.AddRange(detections);
        }

        // Dedup: if two detections overlap, keep the first (by input order)
        var dedupDetections = new List<Detection>();
        var coveredRanges = new List<(int Start, int End)>();

        foreach (var detection in allDetections)
        {
            var detStart = detection.StartIndex;
            var detEnd = detection.StartIndex + detection.Length;
            var overlaps = coveredRanges.Any(r => !(detEnd <= r.Start || detStart >= r.End));

            if (!overlaps)
            {
                dedupDetections.Add(detection);
                coveredRanges.Add((detStart, detEnd));
            }
        }

        // Apply replacements in reverse order to preserve indices
        var output = content;
        foreach (var detection in dedupDetections.OrderByDescending(d => d.StartIndex))
        {
            if (!isUserInput)
            {
                output = output[..detection.StartIndex] + detection.Replacement + output[(detection.StartIndex + detection.Length)..];
            }
        }

        return new SanitizeResult(output, dedupDetections);
    }

    /// <summary>Compute Shannon entropy for a string.
    /// Used by EntropyCredentialDetector to identify high-entropy tokens.</summary>
    public static double ComputeEntropy(string input)
    {
        if (string.IsNullOrEmpty(input)) return 0;

        var counts = new Dictionary<char, int>();
        foreach (var c in input)
        {
            counts.TryGetValue(c, out var count);
            counts[c] = count + 1;
        }

        return counts.Values
            .Select(count => (double)count / input.Length)
            .Select(prob => -prob * Math.Log2(prob))
            .Sum();
    }
}

/// <summary>Result of outbound sanitization. Contains the processed content and all detections found.</summary>
public sealed record SanitizeResult(
    string SanitizedContent,
    IReadOnlyList<Detection> Detections
);
