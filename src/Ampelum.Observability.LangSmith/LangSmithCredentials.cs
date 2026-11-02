using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>
/// Auth + endpoint configuration for <see cref="LangSmithExporter"/>. The
/// <see cref="ApiKey"/> is sent in the <c>X-API-Key</c> header and is never
/// logged or surfaced via <see cref="object.ToString"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangSmithCredentials
{
    /// <summary>LangSmith API key, sent in the <c>X-API-Key</c> request header.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>LangSmith project name, sent in the <c>x-project-name</c> request header.</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>Endpoint base. Defaults to <c>https://api.smith.langchain.com</c>.</summary>
    public Uri Endpoint { get; set; } = new("https://api.smith.langchain.com");

    /// <summary>Returns a redacted representation that never includes the API key.</summary>
    public override string ToString() =>
        $"LangSmithCredentials {{ Project = {ProjectName}, Endpoint = {Endpoint}, ApiKey = *** }}";
}
