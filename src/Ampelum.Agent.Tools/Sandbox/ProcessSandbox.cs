using System.Diagnostics;
using System.Text;

namespace SignalGrid.Ampelum.Agent.Tools;

/// <summary>Process-backed sandbox for trusted local execution.</summary>
public sealed class ProcessSandbox : IExecutionSandbox
{
    public string Kind => "process";

    public async ValueTask<SandboxResult> ExecuteAsync(SandboxCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Kind != SandboxCommandKind.Process)
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Rejected,
                RejectionReason = $"Unsupported command kind: {command.Kind}",
                CorrelationId = command.CorrelationId,
            };

        if (command.Policy.IsolationMode != SandboxIsolationMode.Process)
        {
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Rejected,
                RejectionReason = $"Sandbox isolation mode not supported: {command.Policy.IsolationMode}",
                CorrelationId = command.CorrelationId,
            };
        }

        if (string.IsNullOrWhiteSpace(command.Program))
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Rejected,
                RejectionReason = "Program is required.",
                CorrelationId = command.CorrelationId,
            };

        var workingDirectory = ResolveWorkingDirectory(command.WorkingDirectory, command.Policy);
        if (workingDirectory is null)
        {
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Rejected,
                RejectionReason = "Working directory is outside the allowed roots.",
                CorrelationId = command.CorrelationId,
            };
        }

        if (!Directory.Exists(workingDirectory))
        {
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Failed,
                RejectionReason = $"Working directory not found: {workingDirectory}",
                CorrelationId = command.CorrelationId,
            };
        }

        if (command.Policy.RequiresIsolation)
        {
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Rejected,
                RejectionReason = "ProcessSandbox provides no OS-level network or filesystem " +
                    "isolation and cannot enforce the requested policy (network restriction " +
                    "and/or file-write restriction). Unavailable for untrusted commands until " +
                    "a real isolated backend exists.",
                CorrelationId = command.CorrelationId,
            };
        }

        var timeout = command.Policy.Timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : command.Policy.Timeout;
        var startedAt = DateTimeOffset.UtcNow;
        var psi = new ProcessStartInfo
        {
            FileName = command.Program,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);

        if (!command.Policy.PreserveEnvironment)
            ScrubEnvironment(psi);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new SandboxResult
                {
                    Outcome = SandboxOutcome.Failed,
                    RejectionReason = "Failed to start process.",
                    CorrelationId = command.CorrelationId,
                };
            }

            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return timeoutCts.IsCancellationRequested
                    ? new SandboxResult
                    {
                        Outcome = SandboxOutcome.TimedOut,
                        RejectionReason = $"Command timed out after {timeout.TotalSeconds:0}s",
                        StandardOutput = stdout.ToString().TrimEnd(),
                        StandardError = stderr.ToString().TrimEnd(),
                        CorrelationId = command.CorrelationId,
                    }
                    : new SandboxResult
                    {
                        Outcome = SandboxOutcome.Cancelled,
                        RejectionReason = "Sandbox execution cancelled.",
                        StandardOutput = stdout.ToString().TrimEnd(),
                        StandardError = stderr.ToString().TrimEnd(),
                        CorrelationId = command.CorrelationId,
                    };
            }
            finally
            {
                TryDrain(process);
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            return new SandboxResult
            {
                Outcome = process.ExitCode == 0 ? SandboxOutcome.Succeeded : SandboxOutcome.Failed,
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString().TrimEnd(),
                StandardError = stderr.ToString().TrimEnd(),
                Duration = elapsed,
                CorrelationId = command.CorrelationId,
                RejectionReason = process.ExitCode == 0 ? null : $"Command exited with code {process.ExitCode}",
            };
        }
        catch (Exception ex)
        {
            return new SandboxResult
            {
                Outcome = SandboxOutcome.Failed,
                RejectionReason = ex.Message,
                CorrelationId = command.CorrelationId,
            };
        }
    }

    static string? ResolveWorkingDirectory(string? requested, SandboxPolicy policy)
    {
        var roots = policy.AllowedWorkingDirectories ?? [];
        if (roots.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return Environment.CurrentDirectory;
            return Path.GetFullPath(requested);
        }

        var firstRoot = Path.GetFullPath(roots[0]);
        var target = string.IsNullOrWhiteSpace(requested) ? firstRoot : Path.GetFullPath(requested, firstRoot);

        foreach (var candidateRoot in roots)
        {
            var root = Path.GetFullPath(candidateRoot);
            if (IsWithinRoot(target, root))
                return target;
        }

        return null;
    }

    static bool IsWithinRoot(string target, string root)
    {
        if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    static readonly string[] EnvironmentAllowList =
    [
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "SystemDrive",
        "ComSpec",
        "windir",
        "TEMP",
        "TMP",
        "HOME",
    ];

    static void ScrubEnvironment(ProcessStartInfo psi)
    {
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EnvironmentAllowList)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
                keep[name] = value;
        }

        psi.EnvironmentVariables.Clear();
        foreach (var (name, value) in keep)
            psi.EnvironmentVariables[name] = value;
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    static void TryDrain(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch { }

        try
        {
            process.CancelErrorRead();
        }
        catch { }
    }
}
