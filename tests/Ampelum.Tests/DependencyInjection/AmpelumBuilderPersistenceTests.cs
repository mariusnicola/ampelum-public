using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.DependencyInjection;

public sealed class AmpelumBuilderPersistenceTests
{
    [Fact]
    public void AddProgramPersistence_RegistersIProgramPersistence()
    {
        var services = new ServiceCollection();
        services.AddAmpelum().AddProgramPersistence();
        services.BuildServiceProvider()
            .GetService<IProgramPersistence>()
            .ShouldBeOfType<JsonProgramPersistence>();
    }

    [Fact]
    public void AddProgramPersistence_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddAmpelum().AddProgramPersistence();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IProgramPersistence>()
            .ShouldBeSameAs(sp.GetRequiredService<IProgramPersistence>());
    }

    [Fact]
    public void AddProgramPersistence_CalledTwice_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddAmpelum().AddProgramPersistence().AddProgramPersistence();
        services.BuildServiceProvider()
            .GetServices<IProgramPersistence>().Count().ShouldBe(1);
    }

    [Fact]
    public void AddProgramPersistence_CustomImpl_Preserved()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProgramPersistence, CustomPersistence>();
        services.AddAmpelum().AddProgramPersistence();
        services.BuildServiceProvider()
            .GetRequiredService<IProgramPersistence>()
            .ShouldBeOfType<CustomPersistence>();
    }

    [Fact]
    public void AddProgramPersistence_ReturnsBuilderForChaining()
    {
        var builder = new ServiceCollection().AddAmpelum();
        var returned = builder.AddProgramPersistence();
        returned.ShouldBeSameAs(builder);
    }

    sealed class CustomPersistence : IProgramPersistence
    {
        public Task SaveAsync<T>(OptimizedProgram<T> op, string path, CancellationToken ct = default)
            where T : class => Task.CompletedTask;
        public Task<OptimizedProgram<T>> LoadAsync<T>(string path, CancellationToken ct = default)
            where T : class, new() => Task.FromResult(new OptimizedProgram<T>(
                new T(),
                new Dictionary<string, IReadOnlyList<Example>>(),
                new Dictionary<string, string>()));
    }
}
