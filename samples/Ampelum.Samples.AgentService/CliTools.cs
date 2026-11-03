using System.Diagnostics;
using System.Text.Json;

namespace Ampelum.Samples.AgentService;

/// <summary>
/// <c>ampelum new</c>, run as a process. The command shells out to <c>dotnet new</c>, which only knows a
/// template once it is installed, so the template folder shipped next to the sample is installed first
/// into a template-engine home that belongs to this run. The machine's own template list is not touched.
/// </summary>
public static class CliNew
{
    public const string Template = "ampelum-agent";

    /// <summary>The template folder copied next to the sample's binaries by the project file.</summary>
    public static string TemplateDirectory => Path.Combine(AppContext.BaseDirectory, "templates", Template);

    /// <summary>
    /// Installs the template into <paramref name="engineHome"/>, then runs <c>ampelum new</c> into <paramref name="outputDir"/>.
    /// Returns the first step that failed, or the result of the command.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string projectName, string outputDir, string engineHome, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var limit = timeout ?? CliEval.DefaultTimeout;

        var install = Isolated(new ProcessStartInfo(CliEval.DotnetHostPath()), engineHome);
        foreach (var arg in new[] { "new", "install", TemplateDirectory })
            install.ArgumentList.Add(arg);
        var installed = await CliEval.RunProcessAsync(install, limit, ct);
        if (installed.ExitCode != 0)
            return installed;

        var create = Isolated(CliEval.CliCommand("new", Template, "--name", projectName, "--output", outputDir), engineHome);
        return await CliEval.RunProcessAsync(create, limit, ct);
    }

    /// <summary>Points the template engine at a home of its own so nothing is installed machine-wide.</summary>
    static ProcessStartInfo Isolated(ProcessStartInfo start, string engineHome)
    {
        start.Environment["DOTNET_CLI_HOME"] = engineHome;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return start;
    }
}

/// <summary><c>ampelum repl</c>, run as a process with the key left blank.</summary>
public static class CliRepl
{
    /// <summary>The variable the command reads its key from.</summary>
    public const string ApiKeyVariable = "OPENROUTER_API_KEY";

    /// <summary>Runs the command with <see cref="ApiKeyVariable"/> set to an empty string. It must refuse before any model call.</summary>
    public static async Task<(int ExitCode, string Output)> RunWithBlankKeyAsync(
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var start = CliEval.CliCommand("repl");
        start.Environment[ApiKeyVariable] = "";
        return await CliEval.RunProcessAsync(start, timeout ?? CliEval.DefaultTimeout, ct);
    }
}

/// <summary>The Host's own command line, <c>rebuild-index</c>, run as a process.</summary>
public static class HostRebuildCli
{
    public static string HostAssemblyPath => Path.Combine(AppContext.BaseDirectory, "SignalGrid.Ampelum.Host.dll");

    /// <summary>Writes the Host settings the command reads: where its SQLite file, audit file and prompt folder are.</summary>
    public static async Task WriteConfigAsync(
        string path, string sqlitePath, string auditFile, string promptDir, CancellationToken ct = default)
    {
        var config = new
        {
            Host = new { Storage = new { SqlitePath = sqlitePath, AuditFile = auditFile, PromptDir = promptDir } },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), ct);
    }

    /// <summary>Runs <c>rebuild-index</c> with <paramref name="arguments"/> after it, from <paramref name="workingDirectory"/>.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory, IEnumerable<string> arguments, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(CliEval.DotnetHostPath()) { WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(HostAssemblyPath);
        start.ArgumentList.Add("rebuild-index");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return await CliEval.RunProcessAsync(start, timeout ?? CliEval.DefaultTimeout, ct);
    }
}
