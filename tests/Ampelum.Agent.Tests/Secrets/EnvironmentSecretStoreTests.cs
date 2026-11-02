using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class EnvironmentSecretStoreTests : IDisposable
{
    private const string Prefix = "AMPL_SECRET_";
    private const string TestJti = "env-session-001";

    /// <summary>Set env vars before each test, clean up after.</summary>
    private readonly Dictionary<string, string> _setVars = new(StringComparer.Ordinal);

    public void Dispose()
    {
        foreach (var key in _setVars.Keys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private void SetEnv(string alias, string value)
    {
        var envKey = Prefix + alias;
        Environment.SetEnvironmentVariable(envKey, value);
        _setVars[envKey] = value;
    }

    [Fact]
    public async Task Resolve_ExistingEnvVar_ReturnsValue()
    {
        SetEnv("my-key", "sk-test123");
        var store = new EnvironmentSecretStore();

        var result = await store.ResolveAsync("my-key", TestJti,
            TestContext.Current.CancellationToken);

        result.ShouldBe("sk-test123");
    }

    [Fact]
    public async Task Resolve_NonExistent_ThrowsSecretNotFoundException()
    {
        var store = new EnvironmentSecretStore();

        await Should.ThrowAsync<SecretNotFoundException>(() =>
            store.ResolveAsync("does-not-exist", TestJti,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAliases_ReturnsKeysWithPrefix()
    {
        SetEnv("db-pass", "pwd-456");
        SetEnv("github-token", "ghp-789");
        // Set an unrelated env var WITHOUT the prefix — should not appear in listing
        Environment.SetEnvironmentVariable("MYAPP_OTHER", "should-not-appear");
        _setVars["MYAPP_OTHER"] = "should-not-appear";
        var store = new EnvironmentSecretStore();

        var aliases = await store.ListAliasesAsync(TestJti,
            TestContext.Current.CancellationToken);

        aliases.ShouldBe(new[] { "db-pass", "github-token" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Store_ThrowsReadOnlyException()
    {
        var store = new EnvironmentSecretStore();

        await Should.ThrowAsync<SecretStoreReadOnlyException>(() =>
            store.StoreAsync("some-key", "some-value", TestJti,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_ThrowsReadOnlyException()
    {
        var store = new EnvironmentSecretStore();

        await Should.ThrowAsync<SecretStoreReadOnlyException>(() =>
            store.DeleteAsync("some-key", TestJti,
                TestContext.Current.CancellationToken));
    }
}
