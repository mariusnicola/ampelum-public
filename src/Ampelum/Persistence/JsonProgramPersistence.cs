using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Optimizers;

namespace SignalGrid.Ampelum.Persistence;

/// <summary>
/// JSON-backed <see cref="IProgramPersistence"/>. Uses a temp-file +
/// <see cref="File.Move(string,string,bool)"/> for atomic overwrites. Wire format version 1.
/// </summary>
public sealed class JsonProgramPersistence : IProgramPersistence
{
    const int SupportedVersion = 1;

    static readonly JsonSerializerOptions s_opts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <inheritdoc />
    public async Task SaveAsync<TProgram>(
        OptimizedProgram<TProgram> optimizedProgram,
        string path,
        CancellationToken ct = default)
        where TProgram : class
    {
        ArgumentNullException.ThrowIfNull(optimizedProgram);
        ArgumentNullException.ThrowIfNull(path);

        var wire = new ProgramPersistenceWireFormat
        {
            Version = SupportedVersion,
            TProgramType = typeof(TProgram).FullName ?? typeof(TProgram).Name,
            DemoAssignments = ToDemoAssignments(optimizedProgram.DemosBySubmodule),
            InstructionOverrides =
                new Dictionary<string, string>(optimizedProgram.InstructionOverrides),
        };

        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, wire, s_opts, ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            throw new ProgramPersistenceException(
                $"Failed to save optimized program to '{path}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<OptimizedProgram<TProgram>> LoadAsync<TProgram>(
        string path,
        CancellationToken ct = default)
        where TProgram : class, new()
    {
        ArgumentNullException.ThrowIfNull(path);

        var wire = await ReadWireAsync(path, ct).ConfigureAwait(false);

        return new OptimizedProgram<TProgram>(
            Program: new TProgram(),
            DemosBySubmodule: FromDemoAssignments(wire.DemoAssignments),
            InstructionOverrides: wire.InstructionOverrides);
    }

    /// <summary>
    /// v1.0 ergonomic overload that loads an optimized <see cref="Predict{TSig}"/> program directly,
    /// bypassing the <c>where TProgram : class, new()</c> constraint of <see cref="LoadAsync{TProgram}"/>.
    /// <see cref="Predict{TSig}"/> has no public parameterless constructor (it requires an
    /// <see cref="AmpelumContext"/>), so callers previously had to wrap it in a parameterless shim.
    /// This overload accepts the context directly and seeds the resulting <see cref="OptimizedProgram{TProgram}"/>
    /// with a fresh <see cref="Predict{TSig}"/>; <see cref="OptimizedProgram{TProgram}.Bind"/> rebinds
    /// it via the existing <see cref="Predict{TSig}.WithContext"/> codepath when applied.
    /// </summary>
    /// <typeparam name="TSig">Signature type of the predict module.</typeparam>
    /// <param name="path">Path to the wire-format JSON file produced by <see cref="SaveAsync{TProgram}"/>.</param>
    /// <param name="context">Context used to construct the seed <see cref="Predict{TSig}"/> instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OptimizedProgram{TProgram}"/> wrapping a fresh <see cref="Predict{TSig}"/>
    /// with demos and instruction overrides preserved from disk.</returns>
    /// <exception cref="ProgramPersistenceException">I/O error, parse failure, or version mismatch.</exception>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public async Task<OptimizedProgram<Predict<TSig>>> LoadPredictAsync<TSig>(
        string path,
        AmpelumContext context,
        CancellationToken ct = default)
        where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(context);

        var wire = await ReadWireAsync(path, ct).ConfigureAwait(false);

        return new OptimizedProgram<Predict<TSig>>(
            Program: new Predict<TSig>(context),
            DemosBySubmodule: FromDemoAssignments(wire.DemoAssignments),
            InstructionOverrides: wire.InstructionOverrides);
    }

    async Task<ProgramPersistenceWireFormat> ReadWireAsync(string path, CancellationToken ct)
    {
        ProgramPersistenceWireFormat? wire;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, useAsync: true);
            wire = await JsonSerializer
                .DeserializeAsync<ProgramPersistenceWireFormat>(fs, s_opts, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException ex)
        {
            throw new ProgramPersistenceException($"File not found: '{path}'.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ProgramPersistenceException($"Directory not found for '{path}'.", ex);
        }
        catch (Exception ex)
        {
            throw new ProgramPersistenceException($"Failed to load from '{path}'.", ex);
        }

        if (wire is null)
            throw new ProgramPersistenceException($"File '{path}' deserialised to null.");

        if (wire.Version != SupportedVersion)
            throw new ProgramPersistenceException(
                $"Unsupported version {wire.Version} in '{path}'. Expected {SupportedVersion}.");

        return wire;
    }

    static Dictionary<string, List<WireExample>> ToDemoAssignments(
        IReadOnlyDictionary<string, IReadOnlyList<Example>> demos)
    {
        var r = new Dictionary<string, List<WireExample>>();
        foreach (var (k, list) in demos)
            r[k] = list.Select(ex => new WireExample
            {
                Inputs = new Dictionary<string, object?>(ex.Inputs),
                Outputs = new Dictionary<string, object?>(ex.Outputs),
            }).ToList();
        return r;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<Example>> FromDemoAssignments(
        Dictionary<string, List<WireExample>> raw)
    {
        var r = new Dictionary<string, IReadOnlyList<Example>>();
        foreach (var (k, list) in raw)
        {
            r[k] = list.Select(w => new Example(
                NormaliseJsonValues(w.Inputs),
                NormaliseJsonValues(w.Outputs))).ToList();
        }
        return r;
    }

    static IReadOnlyDictionary<string, object?> NormaliseJsonValues(
        IReadOnlyDictionary<string, object?> raw)
    {
        var dict = new Dictionary<string, object?>(raw.Count);
        foreach (var (k, v) in raw)
            dict[k] = v is JsonElement je ? UnboxJsonElement(je) : v;
        return dict;
    }

    // System.Text.Json normally deserialises JSON `null` directly to a C# `null`
    // reference when the destination is `object`, so we rarely see a JsonElement
    // with ValueKind.Null. Handle it defensively anyway â€” without this arm the
    // default branch would return the literal string "null" via GetRawText().
    internal static object? UnboxJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var i) ? i : je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je.GetRawText(),
    };

    /// <summary>
    /// Best-effort cleanup of a temp file. Internal so tests can drive the
    /// catch-all path directly (the cleanup is invoked from primary error
    /// paths where producing a deterministic delete failure is impractical).
    /// Returns <c>true</c> if a file was deleted, <c>false</c> otherwise.
    /// </summary>
    internal static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
#pragma warning disable CA1031 // intentional swallow â€” best-effort cleanup
        catch
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
