using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SecretStoreTests
{
    private const string TestJti = "session-001";
    private const string TestJti2 = "session-002";

    // ── Basic lifecycle ──────────────────────────────────────────────

    [Fact]
    public async Task StoreAndResolve_ReturnsStoredValue()
    {
        var store = new InMemorySecretStore();

        await store.StoreAsync("api-key", "sk-test123", TestJti);
        var result = await store.ResolveAsync("api-key", TestJti);

        result.ShouldBe("sk-test123");
    }

    [Fact]
    public async Task Resolve_NonExistentAlias_ThrowsSecretNotFoundException()
    {
        var store = new InMemorySecretStore();

        await Should.ThrowAsync<SecretNotFoundException>(
            () => store.ResolveAsync("missing-alias", TestJti));
    }

    [Fact]
    public async Task Exists_ReturnsTrueForStoredAlias()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("github-token", "ghp_abc123", TestJti);

        var exists = await store.ExistsAsync("github-token", TestJti);

        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task Exists_ReturnsFalseForMissingAlias()
    {
        var store = new InMemorySecretStore();

        var exists = await store.ExistsAsync("not-stored", TestJti);

        exists.ShouldBeFalse();
    }

    // ── Session isolation ────────────────────────────────────────────

    [Fact]
    public async Task Secrets_IsolatedBySession()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("my-key", "value-1", TestJti);
        await store.StoreAsync("my-key", "value-2", TestJti2);

        var v1 = await store.ResolveAsync("my-key", TestJti);
        var v2 = await store.ResolveAsync("my-key", TestJti2);

        v1.ShouldBe("value-1");
        v2.ShouldBe("value-2");
    }

    // ── Overwrite ────────────────────────────────────────────────────

    [Fact]
    public async Task Store_OverwritesExistingAlias()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("key", "old", TestJti);
        await store.StoreAsync("key", "new", TestJti);

        var result = await store.ResolveAsync("key", TestJti);

        result.ShouldBe("new");
    }

    // ── Purge ────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeSession_RemovesAllSecrets()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("a", "1", TestJti);
        await store.StoreAsync("b", "2", TestJti);

        await store.PurgeSessionAsync(TestJti);

        (await store.ExistsAsync("a", TestJti)).ShouldBeFalse();
        (await store.ExistsAsync("b", TestJti)).ShouldBeFalse();
    }

    [Fact]
    public async Task PurgeSession_DoesNotAffectOtherSessions()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("key", "session1", TestJti);
        await store.StoreAsync("key", "session2", TestJti2);

        await store.PurgeSessionAsync(TestJti);

        (await store.ResolveAsync("key", TestJti2)).ShouldBe("session2");
    }

    // ── Thread safety ────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAccess_DoesNotCorruptState()
    {
        var store = new InMemorySecretStore();
        var tasks = new List<Task>();

        for (int i = 0; i < 50; i++)
        {
            var alias = $"key-{i}";
            tasks.Add(store.StoreAsync(alias, $"val-{i}", TestJti));
        }
        await Task.WhenAll(tasks);

        for (int i = 0; i < 50; i++)
        {
            var value = await store.ResolveAsync($"key-{i}", TestJti);
            value.ShouldBe($"val-{i}");
        }
    }

    // ── List aliases ────────────────────────────────────────────────

    [Fact]
    public async Task ListAliases_EmptySession_ReturnsEmpty()
    {
        var store = new InMemorySecretStore();

        var aliases = await store.ListAliasesAsync(TestJti);

        aliases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAliases_ReturnsAllAliasesForSession()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("api-key", "sk-123", TestJti);
        await store.StoreAsync("db-pass", "pwd-456", TestJti);
        await store.StoreAsync("github-token", "ghp-789", TestJti);

        var aliases = await store.ListAliasesAsync(TestJti);

        aliases.ShouldBe(new[] { "api-key", "db-pass", "github-token" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListAliases_IsolatedBySession()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("key1", "val1", TestJti);
        await store.StoreAsync("key2", "val2", TestJti2);

        var aliases1 = await store.ListAliasesAsync(TestJti);
        var aliases2 = await store.ListAliasesAsync(TestJti2);

        aliases1.ShouldBe(new[] { "key1" });
        aliases2.ShouldBe(new[] { "key2" });
    }

    // ── Delete ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesStoredAlias()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("key", "value", TestJti);

        await store.DeleteAsync("key", TestJti);

        (await store.ExistsAsync("key", TestJti)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_NonExistentAlias_ThrowsSecretNotFoundException()
    {
        var store = new InMemorySecretStore();

        await Should.ThrowAsync<SecretNotFoundException>(
            () => store.DeleteAsync("missing", TestJti));
    }

    [Fact]
    public async Task Delete_IsolatedBySession()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("key", "session1", TestJti);
        await store.StoreAsync("key", "session2", TestJti2);

        await store.DeleteAsync("key", TestJti);

        (await store.ResolveAsync("key", TestJti2)).ShouldBe("session2");
    }

    [Fact]
    public async Task Delete_ReadOnlyStore_ThrowsSecretStoreReadOnlyException()
    {
        var store = new InMemorySecretStore(readOnly: true);

        await Should.ThrowAsync<SecretStoreReadOnlyException>(
            () => store.DeleteAsync("key", TestJti));
    }

    // ── Read-only ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyStore_ThrowsOnStore()
    {
        var store = new InMemorySecretStore(readOnly: true);

        await Should.ThrowAsync<SecretStoreReadOnlyException>(
            () => store.StoreAsync("key", "val", TestJti));
    }

    [Fact]
    public async Task ReadOnlyStore_AllowsResolve_ThrowsIfNotFound()
    {
        var store = new InMemorySecretStore(readOnly: true);

        await Should.ThrowAsync<SecretNotFoundException>(
            () => store.ResolveAsync("anything", TestJti));
    }
}