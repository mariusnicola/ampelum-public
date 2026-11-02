using System.Collections.Immutable;

namespace SignalGrid.Ampelum.Agent.Modes;

/// <summary>Hardcoded mode tool whitelists per spec §10.1. Sets are read-only.</summary>
public static class ModeWhitelists
{
    /// <summary>Tools allowed in /talk mode (read-only + chat).</summary>
    public static readonly ImmutableHashSet<string> Talk = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "read_file", "glob", "grep", "search", "list_files",
        "ask_followup_question", "reasoning", "chat"
    );

    /// <summary>Tools allowed in /build mode (all tools except admin).</summary>
    public static readonly ImmutableHashSet<string> Build = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "read_file", "write_file", "edit_file", "delete_file",
        "glob", "grep", "search", "list_files",
        "run_shell", "bash", "execute_command",
        "ask_followup_question", "reasoning", "chat",
        "git_commit", "git_diff", "git_push",
        "test", "build"
    );

    /// <summary>Tools allowed in /review mode (read + review + limited shell).</summary>
    public static readonly ImmutableHashSet<string> Review = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "read_file", "glob", "grep", "search", "list_files",
        "run_shell", "ask_followup_question", "reasoning", "chat",
        "git_diff", "git_log"
    );

    /// <summary>Allowed run_shell command roots in review mode.</summary>
    public static readonly ImmutableHashSet<string> ReviewRunShellAllowList = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "git", "dotnet", "npm", "node", "python",
        "ls", "cat", "head", "tail", "grep", "find",
        "diff", "echo", "pwd", "which", "test"
    );
}
