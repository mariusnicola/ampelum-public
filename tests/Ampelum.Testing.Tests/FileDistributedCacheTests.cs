using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Testing.Tests;

public sealed class FileDistributedCacheTests : IDisposable
{
    readonly string _directory;

    public FileDistributedCacheTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ampelum-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Constructor_NullDirectory_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new FileDistributedCache(null!));
    }

    [Fact]
    public async Task Set_Get_Refresh_And_Remove_Cover_Happy_Path()
    {
        var cache = new FileDistributedCache(_directory);
        var key = "api:cache/key";
        var value = new byte[] { 1, 2, 3 };
        var options = new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(10),
        };

        await cache.SetAsync(key, value, options, TestContext.Current.CancellationToken);

        var stored = await cache.GetAsync(key, TestContext.Current.CancellationToken);
        stored.ShouldBe(value);

        await cache.RefreshAsync(key, TestContext.Current.CancellationToken);
        cache.Remove(key);

        var removed = await cache.GetAsync(key, TestContext.Current.CancellationToken);
        removed.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_Returns_Null_When_Key_Is_Missing()
    {
        var cache = new FileDistributedCache(_directory);

        var result = await cache.GetAsync("missing", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_Returns_Null_For_Corrupt_File()
    {
        var cache = new FileDistributedCache(_directory);
        var path = Path.Combine(_directory, "corrupt.json");
        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);

        var result = await cache.GetAsync("corrupt", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_Deletes_Expired_Absolute_Entry()
    {
        var cache = new FileDistributedCache(_directory);
        var key = "absolute";
        await cache.SetAsync(
            key,
            [9, 9],
            new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
            TestContext.Current.CancellationToken);

        var result = await cache.GetAsync(key, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        File.Exists(Path.Combine(_directory, "absolute.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_Deletes_Expired_Sliding_Entry()
    {
        var cache = new FileDistributedCache(_directory);
        var path = Path.Combine(_directory, "sliding.json");
        var expired = new
        {
            Value = Convert.ToBase64String(new byte[] { 4, 5, 6 }),
            AbsoluteExpiration = (DateTimeOffset?)null,
            SlidingExpiration = TimeSpan.FromMinutes(1),
            LastAccessed = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(expired), TestContext.Current.CancellationToken);

        var result = await cache.GetAsync("sliding", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_Touches_Sliding_Entry_When_Still_Valid()
    {
        var cache = new FileDistributedCache(_directory);
        var key = "touch";
        await cache.SetAsync(
            key,
            [7, 8, 9],
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30),
            },
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(Path.Combine(_directory, "touch.json"), TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        var result = await cache.GetAsync(key, TestContext.Current.CancellationToken);
        var after = await File.ReadAllTextAsync(Path.Combine(_directory, "touch.json"), TestContext.Current.CancellationToken);

        result.ShouldBe([7, 8, 9]);
        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task RefreshAsync_Is_NoOp_For_Missing_Key()
    {
        var cache = new FileDistributedCache(_directory);

        await cache.RefreshAsync("missing", TestContext.Current.CancellationToken);

        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_Updates_Existing_Entry()
    {
        var cache = new FileDistributedCache(_directory);
        var key = "refresh";
        await cache.SetAsync(
            key,
            [1],
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) },
            TestContext.Current.CancellationToken);

        var path = Path.Combine(_directory, "refresh.json");
        var before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        await cache.RefreshAsync(key, TestContext.Current.CancellationToken);
        var after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        after.ShouldNotBe(before);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch { }
    }
}

public sealed class ScenarioFixtureTests
{
    sealed class ProbeScenarioFixture : ScenarioFixture
    {
        protected override string ProjectName => "probe";
        protected override string ModelId => "probe-model";
        protected override IChatClient Chat { get; } = new FakeChatClient().EnqueueText("ok");
    }

    sealed class EnvironmentScope : IDisposable
    {
        readonly string _key;
        readonly string? _previous;

        public EnvironmentScope(string key, string? value)
        {
            _key = key;
            _previous = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_key, _previous);
    }

    static object? GetPrivateField(object instance, string name)
    {
        var field = typeof(E2EFixture).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        return field.GetValue(instance);
    }

    [Fact]
    public void ScenarioFixture_Defaults_Env_Flags_To_False()
    {
        var scenario = new ProbeScenarioFixture();

        scenario.IsEnabled.ShouldBeFalse();
        scenario.HasApiKey.ShouldBeFalse();
    }

    [Fact]
    public void ScenarioFixture_Respects_Env_Flags()
    {
        using var enabled = new EnvironmentScope("AMPELUM_E2E_SCENARIOS", "1");
        using var key = new EnvironmentScope("AMPELUM_INTEGRATION_OPENROUTER_KEY", "test-key");

        var scenario = new ProbeScenarioFixture();

        scenario.IsEnabled.ShouldBeTrue();
        scenario.HasApiKey.ShouldBeTrue();
    }

    [Fact]
    public void CreateCacheBackend_Defaults_To_Disk()
    {
        var scenario = new ProbeScenarioFixture();

        var method = typeof(E2EFixture).GetMethod("CreateCacheBackend", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var cache = method.Invoke(scenario, []);
        cache.ShouldBeOfType<FileDistributedCache>();
    }

    [Fact]
    public void CreateCacheBackend_Uses_Memory_When_Configured()
    {
        using var backend = new EnvironmentScope("E2E_CACHE_BACKEND", "memory");
        var scenario = new ProbeScenarioFixture();

        var method = typeof(E2EFixture).GetMethod("CreateCacheBackend", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var cache = method.Invoke(scenario, []);
        cache.ShouldBeOfType<MemoryDistributedCache>();
    }

    [Fact]
    public void CreateCachingContext_Initializes_Cache_And_Chat_Wrapper()
    {
        using var backend = new EnvironmentScope("E2E_CACHE_BACKEND", "memory");
        var scenario = new ProbeScenarioFixture();

        var context = scenario.CreateCachingContext();
        context.ShouldNotBeNull();

        GetPrivateField(scenario, "_cache").ShouldBeOfType<MemoryDistributedCache>();
        GetPrivateField(scenario, "_cachedChat").ShouldNotBeNull();
    }
}
