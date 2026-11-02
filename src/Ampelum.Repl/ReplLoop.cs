using System.Globalization;

namespace SignalGrid.Ampelum.Repl;

/// <summary>
/// Line-driven REPL input loop. Recognises <c>:save &lt;slot&gt;</c>, <c>:load &lt;slot&gt;</c> and
/// <c>:quit</c> meta-commands; everything else is forwarded to <see cref="ReplHost.EvaluateAsync"/>.
/// </summary>
public sealed class ReplLoop
{
    readonly ReplHost _host;
    readonly TextReader _input;
    readonly TextWriter _output;
    readonly TextWriter _error;

    /// <summary>Creates a loop over the supplied I/O streams.</summary>
    /// <param name="host">REPL host carrying the Roslyn script state.</param>
    /// <param name="input">User-input source.</param>
    /// <param name="output">Standard output sink.</param>
    /// <param name="error">Error sink for evaluation failures.</param>
    public ReplLoop(ReplHost host, TextReader input, TextWriter output, TextWriter error)
    {
        _host = host;
        _input = input;
        _output = output;
        _error = error;
    }

    /// <summary>
    /// Runs the loop until either input is exhausted, <c>:quit</c> is read, or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        await _output.WriteLineAsync("Ampelum REPL — type :quit to exit, :save <slot>, :load <slot>.").ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            await _output.WriteAsync("> ").ConfigureAwait(false);
            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == ":quit") return;
            if (line.StartsWith(":save ", StringComparison.Ordinal))
            {
                var slot = line[":save ".Length..].Trim();
                _host.Save(slot);
                await _output.WriteLineAsync($"saved {slot}").ConfigureAwait(false);
                continue;
            }
            if (line.StartsWith(":load ", StringComparison.Ordinal))
            {
                var slot = line[":load ".Length..].Trim();
                var snap = _host.Load(slot);
                await _output.WriteLineAsync(snap is null
                    ? $"no such slot: {slot}"
                    : $"loaded {slot} ({snap.Count} vars)").ConfigureAwait(false);
                continue;
            }
            try
            {
                var value = await _host.EvaluateAsync(line, ct).ConfigureAwait(false);
                if (value is not null) await _output.WriteLineAsync(FormatValue(value)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await _error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    static string FormatValue(object value) => value switch
    {
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
