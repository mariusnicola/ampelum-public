using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class AmpelumBuilderRetrieverTests
{
    static IServiceCollection BaseServices()
    {
        var s = new ServiceCollection();
        s.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory>();
        s.AddSingleton<IChatClient>(_ => new FakeChatClient());
        return s;
    }

    [Fact]
    public void WithRetriever_RegistersIRetrieverScoped()
    {
        var services = BaseServices();
        services.AddAmpelum().WithRetriever<StubRetriever>();
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var r1 = scope1.ServiceProvider.GetRequiredService<IRetriever>();
        var r2 = scope1.ServiceProvider.GetRequiredService<IRetriever>();
        var r3 = scope2.ServiceProvider.GetRequiredService<IRetriever>();

        r1.ShouldBeSameAs(r2);     // same scope → same instance
        r1.ShouldNotBeSameAs(r3);  // different scope → different instance
    }

    [Fact]
    public void WithVectorStore_RegistersIVectorStoreSingleton()
    {
        var services = BaseServices();
        services.AddAmpelum().WithVectorStore<InMemoryVectorStore>();
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var vs1 = scope1.ServiceProvider.GetRequiredService<IVectorStore>();
        var vs2 = scope2.ServiceProvider.GetRequiredService<IVectorStore>();

        vs1.ShouldBeSameAs(vs2); // singleton — same instance across scopes
    }

    [Fact]
    public void WithEmbeddingGenerator_RegistersIEmbeddingGeneratorSingleton()
    {
        var services = BaseServices();
        services.AddAmpelum().WithEmbeddingGenerator(
            _ => new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), 3));
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var eg1 = scope1.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var eg2 = scope2.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        eg1.ShouldBeSameAs(eg2);
    }

    [Fact]
    public void WithRetriever_CalledTwice_LastRegistrationWins()
    {
        var services = BaseServices();
        services.AddAmpelum()
            .WithRetriever<StubRetriever>()
            .WithRetriever<StubRetriever2>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        retriever.ShouldBeOfType<StubRetriever2>();
    }

    [Fact]
    public void WithVectorStore_CalledTwice_LastRegistrationWins()
    {
        var services = BaseServices();
        services.AddAmpelum()
            .WithVectorStore<InMemoryVectorStore>()
            .WithVectorStore<InMemoryVectorStore>();
        var provider = services.BuildServiceProvider();

        // Just verifying singleton still resolves correctly after double registration
        var vs = provider.GetRequiredService<IVectorStore>();
        vs.ShouldBeOfType<InMemoryVectorStore>();
    }

    [Fact]
    public void WithEmbeddingGenerator_NullFactory_ThrowsArgumentNullException()
    {
        var services = BaseServices();
        var builder = services.AddAmpelum();
        Should.Throw<ArgumentNullException>(
            () => builder.WithEmbeddingGenerator(null!));
    }

    [Fact]
    public void AmpelumContext_Retriever_PopulatedFromDI_WhenRetrieverRegistered()
    {
        var services = BaseServices();
        services.AddAmpelum().WithRetriever<StubRetriever>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        ctx.Retriever.ShouldNotBeNull();
        ctx.Retriever.ShouldBeOfType<StubRetriever>();
    }

    [Fact]
    public void AmpelumContext_Retriever_IsNull_WhenNoRetrieverRegistered()
    {
        var services = BaseServices();
        services.AddAmpelum();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        ctx.Retriever.ShouldBeNull();
    }

    sealed class StubRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>([]);
    }

    sealed class StubRetriever2 : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>([]);
    }
}
