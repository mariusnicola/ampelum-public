using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace SignalGrid.Ampelum.Repl;

/// <summary>
/// Roslyn-Scripting REPL host. Maintains an evolving <see cref="ScriptState{Object}"/> across
/// <see cref="EvaluateAsync"/> calls. Pre-imports SignalGrid.Ampelum + SignalGrid.Ampelum.Testing namespaces and
/// references the loaded SignalGrid.Ampelum, SignalGrid.Ampelum.Testing, and Microsoft.Extensions.AI.Abstractions
/// assemblies so users can drive Predict/Signature against a real or fake chat client.
/// </summary>
public sealed class ReplHost
{
    readonly ReplGlobals _globals;
    readonly ScriptOptions _options;
    ScriptState<object>? _state;
    readonly Dictionary<string, object?> _saved = new(StringComparer.Ordinal);

    /// <summary>Creates a host bound to an existing globals instance.</summary>
    /// <param name="globals">Globals exposed to every script (carries <see cref="AmpelumContext"/>).</param>
    public ReplHost(ReplGlobals globals)
    {
        _globals = globals;
        _options = ScriptOptions.Default
            .WithImports(
                "System",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "SignalGrid.Ampelum",
                "SignalGrid.Ampelum.Testing")
            .WithReferences(
                typeof(AmpelumContext).Assembly,
                typeof(Ampelum.Testing.FakeChatClient).Assembly,
                typeof(Microsoft.Extensions.AI.IChatClient).Assembly,
                typeof(ReplGlobals).Assembly);
    }

    /// <summary>
    /// Evaluates one chunk of C# source against the running session and returns the value of the
    /// final expression (or <see langword="null"/> when the chunk yielded no value).
    /// </summary>
    /// <param name="code">The C# script source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The expression value or <see langword="null"/>.</returns>
    public async Task<object?> EvaluateAsync(string code, CancellationToken ct)
    {
        _state = _state is null
            ? await CSharpScript.RunAsync(code, _options, _globals, cancellationToken: ct).ConfigureAwait(false)
            : await _state.ContinueWithAsync(code, _options, ct).ConfigureAwait(false);
        return _state.ReturnValue;
    }

    /// <summary>
    /// Saves all script-defined variables (best-effort: name+value mapping) into the in-memory
    /// session store under <paramref name="slot"/>. v0.9f scope: in-process only — disk persistence
    /// of arbitrary user types is deferred (see plan §10).
    /// </summary>
    /// <param name="slot">Free-form slot name (e.g. a path-like identifier).</param>
    public void Save(string slot)
    {
        if (_state is null)
        {
            _saved[slot] = null;
            return;
        }
        var dict = new Dictionary<string, object?>();
        foreach (var v in _state.Variables) dict[v.Name] = v.Value;
        _saved[slot] = dict;
    }

    /// <summary>Returns the snapshot stored under <paramref name="slot"/>, or <see langword="null"/>.</summary>
    /// <param name="slot">Slot name previously passed to <see cref="Save"/>.</param>
    public IReadOnlyDictionary<string, object?>? Load(string slot)
        => _saved.TryGetValue(slot, out var v) && v is Dictionary<string, object?> d ? d : null;

    /// <summary>Snapshots the live variables of the running session.</summary>
    public IReadOnlyDictionary<string, object?> CurrentVariables()
    {
        var dict = new Dictionary<string, object?>();
        if (_state is null) return dict;
        foreach (var v in _state.Variables) dict[v.Name] = v.Value;
        return dict;
    }
}
