using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>Auth + endpoint configuration for <see cref="LangFuseExporter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangFuseCredentials
{
    /// <summary>LangFuse public key (Basic-auth username).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>LangFuse secret key (Basic-auth password). Never logged.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Endpoint base. Defaults to <c>https://cloud.langfuse.com</c>.</summary>
    public Uri Endpoint { get; set; } = new("https://cloud.langfuse.com");

    /// <summary>Returns the <c>Authorization: Basic ...</c> header value.</summary>
    public string ToBasicAuthHeaderValue() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{PublicKey}:{SecretKey}"));

    /// <summary>Returns a redacted representation that never includes the secret key.</summary>
    public override string ToString() =>
        $"LangFuseCredentials {{ PublicKey = {PublicKey}, SecretKey = ***, Endpoint = {Endpoint} }}";
}
