using System.Text.Json;
using SignalGrid.Ampelum.Agent.Modes;

namespace SignalGrid.Ampelum.Agent.Tools;

/// <summary>Mode-aware tool policy that checks ModeWhitelists per mode.
/// Implements the IToolPolicy interface by converting tool name + args into a decision.
/// The mode is extracted from a mode key in the args dictionary.</summary>
public sealed class ModeAwareToolPolicy : IToolPolicy
{
    /// <summary>Key used to extract the current mode from the args dictionary.</summary>
    public const string ModeArgKey = "__mode__";

    /// <inheritdoc />
    public PolicyDecision Evaluate(ToolManifest tool, AgentIdentity caller, IReadOnlyDictionary<string, object?> args)
    {
        var mode = args.TryGetValue(ModeArgKey, out var modeObj) && modeObj is string modeStr
            ? modeStr
            : "talk";

        var toolName = tool.Name;
        var whitelist = mode.ToLowerInvariant() switch
        {
            "/talk" or "talk" => ModeWhitelists.Talk,
            "/build" or "build" => ModeWhitelists.Build,
            "/review" or "review" => ModeWhitelists.Review,
            _ => ModeWhitelists.Talk
        };

        if (!whitelist.Contains(toolName))
            return PolicyDecision.Blocked(("Tool " + toolName + " is not whitelisted in mode " + mode + "."));

        // For run_shell in review mode, check the command allow list
        if ((mode == "/review" || mode == "review") &&
            toolName.Equals("run_shell", StringComparison.OrdinalIgnoreCase))
        {
            if (!args.TryGetValue("command", out var cmdObj) || cmdObj is not string command)
                return PolicyDecision.Blocked("run_shell requires a command argument.");

            var commandRoot = command.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";

            if (!ModeWhitelists.ReviewRunShellAllowList.Contains(commandRoot))
                return PolicyDecision.Blocked(("Command root " + commandRoot + " is not allowed in review mode."));
        }

        return PolicyDecision.Allowed;
    }
}
