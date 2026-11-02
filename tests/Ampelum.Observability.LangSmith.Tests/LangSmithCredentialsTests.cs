using Xunit;
using SignalGrid.Ampelum.Observability.LangSmith;
using Shouldly;

namespace Ampelum.Observability.LangSmith.Tests;

public sealed class LangSmithCredentialsTests
{
    [Fact]
    public void ToString_RedactsApiKey()
    {
        var c = new LangSmithCredentials { ApiKey = "ls__supersecretkey", ProjectName = "demo" };
        var s = c.ToString();
        s.ShouldNotContain("ls__supersecretkey");
        s.ShouldContain("***");
        s.ShouldContain("demo");
    }

    [Fact]
    public void Endpoint_DefaultsToLangSmithCloud()
    {
        var c = new LangSmithCredentials { ApiKey = "k", ProjectName = "p" };
        c.Endpoint.ToString().ShouldStartWith("https://api.smith.langchain.com");
    }
}
