using Xunit;
using System.Text;
using SignalGrid.Ampelum.Observability.LangFuse;
using Shouldly;

namespace Ampelum.Observability.LangFuse.Tests;

public sealed class LangFuseCredentialsTests
{
    [Fact]
    public void ToString_RedactsSecretKey()
    {
        var c = new LangFuseCredentials { PublicKey = "pk_x", SecretKey = "sk_supersecret" };
        var s = c.ToString();
        s.ShouldNotContain("sk_supersecret");
        s.ShouldContain("***");
        s.ShouldContain("pk_x");
    }

    [Fact]
    public void ToBasicAuthHeaderValue_EncodesPublicAndSecret()
    {
        var c = new LangFuseCredentials { PublicKey = "pk", SecretKey = "sk" };
        var v = c.ToBasicAuthHeaderValue();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(v));
        decoded.ShouldBe("pk:sk");
    }

    [Fact]
    public void Endpoint_DefaultsToCloudLangFuse()
    {
        new LangFuseCredentials().Endpoint.ToString().ShouldStartWith("https://cloud.langfuse.com");
    }
}
