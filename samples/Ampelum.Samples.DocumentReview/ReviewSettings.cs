using Ampelum.Samples.Shared;

namespace Ampelum.Samples.DocumentReview;

internal enum RerankerKind
{
    Cohere,
    Voyage,
}

/// <summary>
/// Settings for the parts of the review that talk to a remote service. Each accessor reads only
/// the names its own part needs, so a part whose settings are missing fails alone and by name.
/// </summary>
internal sealed class ReviewSettings(Func<string, string> require)
{
    public const string RerankerName = "REVIEW_RERANKER";
    public const string RerankerKeyName = "REVIEW_RERANKER_KEY";
    public const string RerankerModelName = "REVIEW_RERANKER_MODEL";
    public const string AgentUrlName = "REVIEW_A2A_URL";
    public const string BatchKeyName = "REVIEW_BATCH_KEY";
    public const string BatchModelName = "REVIEW_BATCH_MODEL";

    public static ReviewSettings From(SampleConfig config) => new(config.Require);

    /// <summary>Reads the reranker choice, its key and its model id.</summary>
    public (RerankerKind Kind, string Key, string Model) Reranker()
    {
        var raw = require(RerankerName);
        var kind = raw.Trim().ToLowerInvariant() switch
        {
            "cohere" => RerankerKind.Cohere,
            "voyage" => RerankerKind.Voyage,
            _ => throw new InvalidOperationException($"Setting {RerankerName} must be cohere or voyage."),
        };
        return (kind, require(RerankerKeyName), require(RerankerModelName));
    }

    /// <summary>Reads the reranker key alone, for the offline run that exercises both rerankers whatever the choice says.</summary>
    public string RerankerKey() => require(RerankerKeyName);

    /// <summary>Reads the reranker model id alone, for the same offline run.</summary>
    public string RerankerModel() => require(RerankerModelName);

    public Uri AgentUrl()
    {
        var raw = require(AgentUrlName);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Setting {AgentUrlName} must be an absolute http or https URL.");
        return uri;
    }

    public (string Key, string Model) Batch() => (require(BatchKeyName), require(BatchModelName));
}
