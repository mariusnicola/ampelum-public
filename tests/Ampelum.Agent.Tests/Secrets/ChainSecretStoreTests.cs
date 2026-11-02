using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class ChainSecretStoreTests
{
    private const string TestJti = "chain-session-001";

    [Fact]
    public async Task Resolve_TriesEachInOrder_ReturnsFirstHit()
    {
        var store1 = new InMemorySecretStore();
        var store2 = new InMemorySecretStore();
        await store1.StoreAsync("key-a", "from-store1", TestJti,
            TestContext.Current.CancellationToken);
        // store2 also has "key-a" — should not be consulted
        await store2.StoreAsync("key-a", "from-store2", TestJti,
            TestContext.Current.CancellationToken);
        var chain = new ChainSecretStore(new[] { store1, store2 });

        var result = await chain.ResolveAsync("key-a", TestJti,
            TestContext.Current.CancellationToken);

        result.ShouldBe("from-store1");
    }

    [Fact]
    public async Task Resolve_AllMiss_ThrowsSecretNotFoundException()
    {
        var store1 = new InMemorySecretStore();
        var store2 = new InMemorySecretStore();
        var chain = new ChainSecretStore(new[] { store1, store2 });

        await Should.ThrowAsync<SecretNotFoundException>(() =>
            chain.ResolveAsync("missing", TestJti,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Store_WritesToPrimaryOnly()
    {
        var store1 = new InMemorySecretStore();
        var store2 = new InMemorySecretStore();
        var chain = new ChainSecretStore(new[] { store1, store2 });

        await chain.StoreAsync("key-p", "primary-value", TestJti,
            TestContext.Current.CancellationToken);

        // store1 should have it
        (await store1.ExistsAsync("key-p", TestJti,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        // store2 should NOT have it
        (await store2.ExistsAsync("key-p", TestJti,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_DeletesFromAll()
    {
        var store1 = new InMemorySecretStore();
        var store2 = new InMemorySecretStore();
        await store1.StoreAsync("shared-key", "val", TestJti,
            TestContext.Current.CancellationToken);
        await store2.StoreAsync("shared-key", "val", TestJti,
            TestContext.Current.CancellationToken);
        var chain = new ChainSecretStore(new[] { store1, store2 });

        await chain.DeleteAsync("shared-key", TestJti,
            TestContext.Current.CancellationToken);

        (await store1.ExistsAsync("shared-key", TestJti,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await store2.ExistsAsync("shared-key", TestJti,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task ListAliases_UnionsAll_Deduplicated()
    {
        var store1 = new InMemorySecretStore();
        var store2 = new InMemorySecretStore();
        await store1.StoreAsync("key-a", "val", TestJti,
            TestContext.Current.CancellationToken);
        await store1.StoreAsync("key-b", "val", TestJti,
            TestContext.Current.CancellationToken);
        await store2.StoreAsync("key-b", "val", TestJti,
            TestContext.Current.CancellationToken);
        await store2.StoreAsync("key-c", "val", TestJti,
            TestContext.Current.CancellationToken);
        var chain = new ChainSecretStore(new[] { store1, store2 });

        var aliases = await chain.ListAliasesAsync(TestJti,
            TestContext.Current.CancellationToken);

        aliases.ShouldBe(new[] { "key-a", "key-b", "key-c" }, ignoreOrder: true);
    }
}
