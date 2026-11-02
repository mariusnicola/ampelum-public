#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class AmpelumHostOptionsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var o = new AmpelumHostOptions();
        o.Port.ShouldBe(5170);
        o.Url.ShouldBe("http://localhost:5170");
        o.Storage.SqlitePath.ShouldBe("./data/host.db");
        o.Storage.AuditFile.ShouldBe("./data/audit.jsonl");
        o.Storage.PromptDir.ShouldBe("./data/prompts");
        o.OpenRouter.DefaultModel.ShouldBe(string.Empty);
        o.Sse.KeepAliveSeconds.ShouldBe(15);
        o.Sse.FirstTokenDeadlineMs.ShouldBe(1500);
        o.Audit.ChainSchemaVersion.ShouldBe(2);
        o.AdminEndpointsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Bind_FromConfiguration_ReadsAllNestedValues()
    {
        var json = """
        {
          "Host": {
            "Port": 6000,
            "Storage": { "SqlitePath": "./d/db.sqlite", "AuditFile": "./d/audit.jsonl", "PromptDir": "./d/prompts" },
            "OpenRouter": { "ApiKey": "sk-x", "DefaultModel": "x/y" },
            "Sse": { "KeepAliveSeconds": 30, "FirstTokenDeadlineMs": 800 },
            "Audit": { "ChainSchemaVersion": 2 },
            "AdminEndpointsEnabled": true
          }
        }
        """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cfg = new ConfigurationBuilder().AddJsonStream(ms).Build();
        var opts = new AmpelumHostOptions();
        cfg.GetSection(AmpelumHostOptions.SectionName).Bind(opts);
        opts.Port.ShouldBe(6000);
        opts.Storage.SqlitePath.ShouldBe("./d/db.sqlite");
        opts.OpenRouter.ApiKey.ShouldBe("sk-x");
        opts.OpenRouter.DefaultModel.ShouldBe("x/y");
        opts.Sse.KeepAliveSeconds.ShouldBe(30);
        opts.Sse.FirstTokenDeadlineMs.ShouldBe(800);
        opts.AdminEndpointsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void EnvVarOverride_AcceptsDoubleUnderscoreSeparator()
    {
        var json = """{"Host":{"Port":5170}}""";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cfg = new ConfigurationBuilder()
            .AddJsonStream(ms)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Host:Port"] = "7000",
                ["Host:OpenRouter:ApiKey"] = "sk-env",
            })
            .Build();
        var opts = new AmpelumHostOptions();
        cfg.GetSection("Host").Bind(opts);
        opts.Port.ShouldBe(7000);
        opts.OpenRouter.ApiKey.ShouldBe("sk-env");
    }
}
