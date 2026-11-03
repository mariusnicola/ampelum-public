using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>
/// Tags a change with fields that are named at run time, for example by a product team that wants one more
/// column in the release overview. No record type exists for them, so the signature is built from the names.
/// </summary>
public sealed class ChangeTagger(AmpelumContext ctx, ReleaseNotesOptions options)
{
    public const string Instructions = "Tag the change. Answer every output field in one or two words.";

    public async Task<IReadOnlyDictionary<string, string>> TagAsync(ChangeRow row, CancellationToken ct = default)
    {
        var signature = new SignatureMetadata(
            typeof(Dictionary<string, object?>),
            Instructions,
            [
                new Field("Title", typeof(string), "Title of the change.", FieldKind.Input, IsOptional: false),
                new Field("Details", typeof(string), "Details of the change.", FieldKind.Input, IsOptional: false),
            ],
            options.TagFields.Select(name =>
                new Field(name, typeof(string), $"The {name} of the change.", FieldKind.Output, IsOptional: false)).ToList());

        var result = await new DynamicPredict(ctx).ForwardAsync(
            signature,
            new Dictionary<string, object?> { ["Title"] = row.Title, ["Details"] = row.Details },
            ct: ct).ConfigureAwait(false);
        return result.Value.ToDictionary(
            pair => pair.Key,
            pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }
}
