using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SignalGrid.Ampelum.Host.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Host.Internal;

[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed record PromptVersionFileShape(
    int Version,
    [property: JsonPropertyName("_meta")] PromptMeta Meta,
    [property: JsonPropertyName("tProgramType")] string TProgramType,
    [property: JsonPropertyName("demoAssignments")] IReadOnlyDictionary<string, IReadOnlyList<DemoExampleShape>> DemoAssignments,
    [property: JsonPropertyName("instructionOverrides")] IReadOnlyDictionary<string, string> InstructionOverrides);

[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed record PromptMeta(
    [property: JsonPropertyName("versionId")] string VersionId,
    [property: JsonPropertyName("moduleName")] string ModuleName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("parentVersionId")] string? ParentVersionId,
    [property: JsonPropertyName("note")] string? Note);

[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed record DemoExampleShape(
    [property: JsonPropertyName("inputs")] IReadOnlyDictionary<string, JsonElement> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyDictionary<string, JsonElement> Outputs);

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class PromptVersionFileReader
{
    public static async ValueTask WriteAsync(
        string path, PromptVersionFileShape shape, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(fs, shape,
                AmpelumHostJsonContext.Default.PromptVersionFileShape, ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The "_meta" block for a version: the same fields the optimizer writes.</summary>
    public static JsonObject MetaNode(PromptVersionRow row, string status) => new()
    {
        ["versionId"] = row.VersionId,
        ["moduleName"] = row.ModuleName,
        ["status"] = status,
        ["createdAt"] = JsonValue.Create(row.CreatedAt),
        ["parentVersionId"] = row.ParentVersionId,
        ["note"] = row.Note,
    };

    /// <summary>Writes the file by temp file and move, so a reader never sees half a file.</summary>
    public static async ValueTask WriteNodeAsync(string path, JsonObject node, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, node.ToJsonString(), ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Records the version's status in its file's "_meta" block and leaves the rest of the file as it is.
    /// A file that is gone is skipped.
    /// </summary>
    public static async ValueTask WriteStatusAsync(PromptVersionRow row, string status, CancellationToken ct)
    {
        if (!File.Exists(row.FilePath)) return;
        var node = JsonNode.Parse(await File.ReadAllTextAsync(row.FilePath, ct))!.AsObject();
        node["_meta"] = MetaNode(row, status);
        await WriteNodeAsync(row.FilePath, node, ct);
    }

    /// <summary>
    /// The fields of a demonstration's "inputs" or "outputs" object, read under the camel case name or the Pascal case
    /// name an earlier Host wrote. Empty when the example has neither.
    /// </summary>
    public static Dictionary<string, JsonElement> Fields(JsonElement example, string camelCaseName)
    {
        var fields = new Dictionary<string, JsonElement>();
        var pascalCaseName = char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];
        if ((example.TryGetProperty(camelCaseName, out var found) || example.TryGetProperty(pascalCaseName, out found))
            && found.ValueKind == JsonValueKind.Object)
            foreach (var p in found.EnumerateObject())
                fields[p.Name] = p.Value.Clone();
        return fields;
    }

    public static async ValueTask<PromptVersionFileShape> ReadAsync(
        string path, CancellationToken ct,
        ILogger? logger = null)
    {
        await using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Use JsonDocument to handle missing _meta gracefully
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
        var root = doc.RootElement;

        int version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
        var tProgramType = root.TryGetProperty("tProgramType", out var tp) ? tp.GetString() ?? "" : "";

        PromptMeta meta;
        if (root.TryGetProperty("_meta", out var metaEl))
        {
            meta = JsonSerializer.Deserialize(metaEl.GetRawText(),
                AmpelumHostJsonContext.Default.PromptMeta)!;
        }
        else
        {
            logger?.LogWarning("PromptVersionFileReader: file {Path} has no _meta block; Status defaults to 'unknown'.", path);
            meta = new PromptMeta("unknown", "unknown", "unknown", DateTimeOffset.MinValue, null, null);
        }

        var demos = new Dictionary<string, IReadOnlyList<DemoExampleShape>>();
        if (root.TryGetProperty("demoAssignments", out var da))
        {
            foreach (var prop in da.EnumerateObject())
            {
                var list = new List<DemoExampleShape>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    list.Add(new DemoExampleShape(Fields(item, "inputs"), Fields(item, "outputs")));
                }
                demos[prop.Name] = list;
            }
        }

        var overrides = new Dictionary<string, string>();
        if (root.TryGetProperty("instructionOverrides", out var io))
            foreach (var prop in io.EnumerateObject())
                overrides[prop.Name] = prop.Value.GetString() ?? "";

        return new PromptVersionFileShape(version, meta, tProgramType, demos, overrides);
    }
}
