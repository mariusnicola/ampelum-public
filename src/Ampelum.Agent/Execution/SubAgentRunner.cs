using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Agent.Execution;

/// <summary>
/// Runs one coding task in a fresh IChatClient loop with file+build tools
/// and returns a structured result. Uses a DONE: marker pattern to detect completion.
/// Configurable max turns.
/// </summary>
public sealed class SubAgentRunner
{
    const int DefaultMaxTurns = 30;
    const string CompletionMarker = "DONE:";

    readonly IChatClient _chatClient;
    readonly string _workingDir;
    readonly string _sessionId;
    readonly int _maxTurns;
    readonly SessionBudgetMeter? _budget;
    readonly Func<ModelTier, IChatClient>? _clientForTier;

    // clientForTier picks the client for SubAgentTask.Tier; without it every task runs on chatClient.
    public SubAgentRunner(
        IChatClient chatClient,
        string workingDir,
        string sessionId,
        int maxTurns = DefaultMaxTurns,
        SessionBudgetMeter? budget = null,
        Func<ModelTier, IChatClient>? clientForTier = null)
    {
        _budget = budget;
        _clientForTier = clientForTier;
        _chatClient = WithToolInvocation(chatClient ?? throw new ArgumentNullException(nameof(chatClient)));
        _workingDir = workingDir ?? throw new ArgumentNullException(nameof(workingDir));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _maxTurns = maxTurns > 0 ? maxTurns : DefaultMaxTurns;
    }

    // The tool calls a model makes must be run by something. A client that already does (a FunctionInvokingChatClient
    // anywhere in its stack) is used as it is, so no call is run twice.
    static IChatClient WithToolInvocation(IChatClient client) =>
        client.GetService<FunctionInvokingChatClient>() is null ? new FunctionInvokingChatClient(client) : client;

    /// <summary>Run a single subagent task. Returns a structured result.</summary>
    public async Task<SubAgentResult> RunAsync(SubAgentTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var filesChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        // A run counts as one call; its turns add their tokens, and a spent token, byte or duration limit ends the
        // run between turns. The call count never does: a run that got its slot may finish.
        if (_budget is not null && !_budget.TryReserveCall())
            return BudgetStop(task, filesChanged, warnings);

        var client = _clientForTier is null ? _chatClient : WithToolInvocation(_clientForTier(task.Tier));
        var tools = BuildTools(filesChanged);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(task, tools)),
        };

        var options = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto };

        try
        {
            for (int turn = 1; turn <= _maxTurns; turn++)
            {
                if (turn > 1 && _budget is { IsExhaustedExceptCalls: true })
                    return BudgetStop(task, filesChanged, warnings);

                var userText = turn == 1
                    ? task.Description
                    : "Continue. When your task is complete, end your final message with:\nDONE: <2-3 sentence summary>";

                history.Add(new ChatMessage(ChatRole.User, userText));

                var lmContext = ResolveContext(client);
                var chatResponse = await HookedLmCall.InvokeAsync(lmContext, "SubAgentRunner.Turn", history, options, ct)
                    .ConfigureAwait(false);
                _budget?.AddTokens((chatResponse.Usage?.InputTokenCount ?? 0) + (chatResponse.Usage?.OutputTokenCount ?? 0));
                var response = chatResponse.Text ?? "";
                history.Add(new ChatMessage(ChatRole.Assistant, response));

                var doneIdx = FindDoneMarker(response);
                if (doneIdx >= 0)
                {
                    var outcome = response[(doneIdx + CompletionMarker.Length)..].Trim();
                    return new SubAgentResult
                    {
                        TaskId = task.Id,
                        Success = true,
                        FilesChanged = [.. filesChanged],
                        Outcome = outcome,
                        Warnings = [.. warnings],
                        LogId = _sessionId,
                    };
                }
            }

            return new SubAgentResult
            {
                TaskId = task.Id,
                Success = false,
                FilesChanged = [.. filesChanged],
                Outcome = $"Hit {_maxTurns}-turn limit.",
                Warnings = [.. warnings],
                LogId = _sessionId,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SubAgentResult
            {
                TaskId = task.Id,
                Success = false,
                FilesChanged = [.. filesChanged],
                Outcome = ex.Message,
                Warnings = [.. warnings],
                LogId = _sessionId,
            };
        }
    }

    SubAgentResult BudgetStop(SubAgentTask task, HashSet<string> filesChanged, List<string> warnings) => new()
    {
        TaskId = task.Id,
        Success = false,
        FilesChanged = [.. filesChanged],
        Outcome = "Session budget exhausted.",
        Warnings = [.. warnings],
        LogId = _sessionId,
    };

    // Resolves the AmpelumContext HookedLmCall runs its hook pipeline through.
    //
    // SubAgentRunner's _chatClient is deliberately a raw IChatClient distinct from the main
    // Ampelum pipeline (see class doc), so this always calls through _chatClient rather than
    // AmpelumContext.Ambient.Chat (unlike Predict.cs, which resolves the chat client itself from
    // the context). If an AmpelumContext.Ambient scope is active (e.g. AmpelumContext.Push around
    // the subagent dispatch call), its Hooks/Adapter/TraceSink/LoggerFactory are reused so the
    // subagent's LM calls participate in the same hook pipeline; otherwise Hooks stays null and
    // HookedLmCall.InvokeAsync short-circuits to a plain _chatClient.GetResponseAsync call,
    // matching this method's pre-migration behavior exactly.
    AmpelumContext ResolveContext(IChatClient client)
    {
        var ambient = AmpelumContext.Ambient;
        return new AmpelumContext(
            client,
            ambient?.Adapter ?? new ChatAdapter(),
            ambient?.TraceSink ?? NullTraceSink.Instance,
            ambient?.LoggerFactory ?? NullLoggerFactory.Instance)
        {
            Hooks = ambient?.Hooks,
        };
    }

    static int FindDoneMarker(string text) =>
        text.LastIndexOf(CompletionMarker, StringComparison.Ordinal);

    static string BuildSystemPrompt(SubAgentTask task, List<AITool> tools) => $"""
You are a coding subagent. Working directory: {task.Id}
Task: {task.Description}
Tools: {string.Join(", ", tools.Select(t => t.Name))}
When your task is complete, end your final message with:
DONE: <2-3 sentence summary of what you did and the outcome>
""";

    List<AITool> BuildTools(HashSet<string> filesChanged)
    {
        // A path is the working directory's own or nothing: absolute paths and ".." are fine while they stay inside.
        bool Inside(string fullPath)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(_workingDir), Path.GetFullPath(fullPath));
            return !(Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
        }

        string Resolve(string path)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetFullPath(_workingDir), path));
            if (!Inside(full))
                throw new UnauthorizedAccessException($"path is outside the working directory: {path}");
            return full;
        }

        // A search pattern may carry a "..\" of its own, so what a search found is checked again: a result outside
        // the working directory is dropped, whatever the pattern held.
        IEnumerable<string> InsideOnly(IEnumerable<string> found) => found.Where(f => Inside(f));

        return
        [
            AIFunctionFactory.Create(
                (string path, int? offset, int? limit) =>
                {
                    try
                    {
                        if (!File.Exists(Resolve(path)))
                            return $"ERROR: File not found: {path}";
                        var text = File.ReadAllText(Resolve(path));
                        if (offset.HasValue || limit.HasValue)
                        {
                            var lines = text.Split('\n');
                            var start = Math.Max(0, (offset ?? 1) - 1);
                            var count = limit ?? lines.Length - start;
                            var selected = lines.Skip(start).Take(count);
                            return string.Join("\n", selected.Select((l, i) => $"{start + i + 1}|{l}"));
                        }
                        return text;
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR: {ex.Message}";
                    }
                },
                "read_file", "Read a file with line numbers."),

            AIFunctionFactory.Create(
                (string path, string content, bool? append) =>
                {
                    try
                    {
                        var resolved = Resolve(path);
                        var dir = Path.GetDirectoryName(resolved);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (append == true)
                            File.AppendAllText(resolved, content);
                        else
                            File.WriteAllText(resolved, content);
                        filesChanged.Add(resolved);
                        return $"OK: wrote {content.Length} bytes to {path}";
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR: {ex.Message}";
                    }
                },
                "write_file", "Write content to a file."),

            AIFunctionFactory.Create(
                (string path, string? pattern) =>
                {
                    try
                    {
                        var resolved = Resolve(path);
                        if (!Directory.Exists(resolved))
                            return $"ERROR: Directory not found: {path}";
                        var entries = InsideOnly(string.IsNullOrEmpty(pattern)
                            ? Directory.GetFileSystemEntries(resolved)
                            : Directory.GetFileSystemEntries(resolved, pattern));
                        return string.Join("\n", entries.Select(e =>
                        {
                            var isDir = Directory.Exists(e);
                            var info = new FileInfo(e);
                            return $"{(isDir ? "DIR" : "   ")}  {Path.GetRelativePath(_workingDir, e),-50} {info.Length,10:N0}";
                        }));
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR: {ex.Message}";
                    }
                },
                "list_directory", "List files and directories."),

            AIFunctionFactory.Create(
                (string pattern, string? basePath) =>
                {
                    try
                    {
                        var root = basePath is null ? _workingDir : Resolve(basePath);
                        if (!Directory.Exists(root))
                            return $"ERROR: Directory not found: {basePath ?? _workingDir}";
                        var matches = InsideOnly(Directory.GetFiles(root, pattern, SearchOption.AllDirectories)).ToArray();
                        return matches.Length == 0
                            ? "No files matched."
                            : string.Join("\n", matches.Select(f => Path.GetRelativePath(_workingDir, f)));
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR: {ex.Message}";
                    }
                },
                "glob", "Find files by glob pattern."),

            AIFunctionFactory.Create(
                (string pattern, string? basePath, string? fileGlob) =>
                {
                    try
                    {
                        var root = basePath is null ? _workingDir : Resolve(basePath);
                        if (!Directory.Exists(root))
                            return $"ERROR: Directory not found";
                        var files = InsideOnly(string.IsNullOrEmpty(fileGlob)
                            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                            : Directory.GetFiles(root, fileGlob, SearchOption.AllDirectories));
                        var results = new List<string>();
                        foreach (var f in files)
                        {
                            var lines = File.ReadAllLines(f);
                            for (int i = 0; i < lines.Length; i++)
                            {
                                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                                    results.Add($"{Path.GetRelativePath(_workingDir, f)}:{i + 1}: {lines[i].Trim()}");
                            }
                        }
                        return results.Count == 0
                            ? "No matches."
                            : string.Join("\n", results.Take(100));
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR: {ex.Message}";
                    }
                },
                "grep", "Search file contents by substring."),
        ];
    }
}

/// <summary>A task dispatched to a subagent.</summary>
public sealed record SubAgentTask
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public string[] DependsOn { get; init; } = [];
    public string[] FilesHint { get; init; } = [];
    public ModelTier Tier { get; init; } = ModelTier.Fast;
}

/// <summary>Structured result returned by a SubAgentRunner on completion.</summary>
public sealed record SubAgentResult
{
    public required string TaskId { get; init; }
    public bool Success { get; init; }
    public string[] FilesChanged { get; init; } = [];
    public required string Outcome { get; init; }
    public string[] Warnings { get; init; } = [];
    public string? LogId { get; init; }
}
