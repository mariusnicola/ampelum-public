using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SecretRefResolverTests
{
    private const string TestJti = "session-001";

    // ── Extract secret-ref fields ────────────────────────────────────

    [Fact]
    public void ExtractSecretRefFields_NullSchema_ReturnsEmpty()
    {
        var fields = SecretRefResolver.ExtractSecretRefFields(null);

        fields.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractSecretRefFields_NoProperties_ReturnsEmpty()
    {
        var schema = JsonSerializer.SerializeToElement(new { });
        var fields = SecretRefResolver.ExtractSecretRefFields(schema);

        fields.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractSecretRefFields_WithSecretRefFields_ReturnsThose()
    {
        var schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""url"": { ""type"": ""string"" },
                ""authorization"": { ""type"": ""string"", ""format"": ""secret-ref"" },
                ""apiKey"": { ""type"": ""string"", ""format"": ""secret-ref"" },
                ""timeout"": { ""type"": ""integer"" }
            }
        }").RootElement;

        var fields = SecretRefResolver.ExtractSecretRefFields(schema);

        fields.ShouldBe(new[] { "authorization", "apiKey" }, ignoreOrder: true);
    }

    // ── Resolve secrets ──────────────────────────────────────────────

    [Fact]
    public async Task ResolveSecretsAsync_NoSecretRefFields_ReturnsCopyOfArgs()
    {
        var store = new InMemorySecretStore();
        var resolver = new SecretRefResolver(store);
        var args = new Dictionary<string, JsonElement>
        {
            { "url", JsonSerializer.SerializeToElement("https://api.github.com") }
        };

        var result = await resolver.ResolveSecretsAsync(
            new HashSet<string>(), args, TestJti);

        result.ShouldContainKey("url");
        result["url"].GetString().ShouldBe("https://api.github.com");
    }

    [Fact]
    public async Task ResolveSecretsAsync_ResolvesSecretRefFields()
    {
        var store = new InMemorySecretStore();
        var resolver = new SecretRefResolver(store);
        await store.StoreAsync("github-token", "ghp_secret123", TestJti);

        var args = new Dictionary<string, JsonElement>
        {
            { "url", JsonSerializer.SerializeToElement("https://api.github.com") },
            { "authorization", JsonSerializer.SerializeToElement("github-token") }
        };

        var result = await resolver.ResolveSecretsAsync(
            new HashSet<string> { "authorization" }, args, TestJti);

        result["url"].GetString().ShouldBe("https://api.github.com");
        result["authorization"].GetString().ShouldBe("ghp_secret123");
    }

    [Fact]
    public async Task ResolveSecretsAsync_MissingSecret_ThrowsSecretNotFoundException()
    {
        var store = new InMemorySecretStore();
        var resolver = new SecretRefResolver(store);
        var args = new Dictionary<string, JsonElement>
        {
            { "authorization", JsonSerializer.SerializeToElement("missing-token") }
        };

        await Should.ThrowAsync<SecretNotFoundException>(() =>
            resolver.ResolveSecretsAsync(
                new HashSet<string> { "authorization" }, args, TestJti));
    }

    [Fact]
    public async Task ResolveSecretsAsync_MultipleSecretRefFields_ResolvesAll()
    {
        var store = new InMemorySecretStore();
        var resolver = new SecretRefResolver(store);
        await store.StoreAsync("github-token", "ghp_abc123", TestJti);
        await store.StoreAsync("db-pass", "postgres-pwd", TestJti);

        var args = new Dictionary<string, JsonElement>
        {
            { "url", JsonSerializer.SerializeToElement("https://api.github.com") },
            { "authorization", JsonSerializer.SerializeToElement("github-token") },
            { "password", JsonSerializer.SerializeToElement("db-pass") }
        };

        var result = await resolver.ResolveSecretsAsync(
            new HashSet<string> { "authorization", "password" }, args, TestJti);

        result["authorization"].GetString().ShouldBe("ghp_abc123");
        result["password"].GetString().ShouldBe("postgres-pwd");
    }

    [Fact]
    public async Task ResolveSecretsAsync_MissingSecretRefField_SkipsIt()
    {
        var store = new InMemorySecretStore();
        var resolver = new SecretRefResolver(store);
        var args = new Dictionary<string, JsonElement>
        {
            { "url", JsonSerializer.SerializeToElement("https://api.github.com") }
        };

        var result = await resolver.ResolveSecretsAsync(
            new HashSet<string> { "authorization" }, args, TestJti);

        result.ShouldContainKey("url");
        result.ShouldNotContainKey("authorization");
    }

    // ── Clear resolved secrets ───────────────────────────────────────

    [Fact]
    public void ClearResolvedSecrets_ZerosOutSecretValues()
    {
        var resolved = new Dictionary<string, JsonElement>
        {
            { "url", JsonSerializer.SerializeToElement("https://api.github.com") },
            { "authorization", JsonSerializer.SerializeToElement("ghp_secret123") }
        };

        SecretRefResolver.ClearResolvedSecrets(
            new HashSet<string> { "authorization" }, resolved);

        resolved["authorization"].GetString().ShouldBeEmpty();
        resolved["url"].GetString().ShouldBe("https://api.github.com");
    }

    [Fact]
    public void ClearResolvedSecrets_NonStringSecrets_SkipsThem()
    {
        var resolved = new Dictionary<string, JsonElement>
        {
            { "count", JsonSerializer.SerializeToElement(42) },
            { "token", JsonSerializer.SerializeToElement("secret") }
        };

        SecretRefResolver.ClearResolvedSecrets(
            new HashSet<string> { "count", "token" }, resolved);

        resolved["count"].GetInt32().ShouldBe(42);
        resolved["token"].GetString().ShouldBeEmpty();
    }
}
