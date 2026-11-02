using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class EnsembleTests
{
    [Fact]
    public void Ensemble_DoesNotImplementIOptimizerOfTProgram() =>
        typeof(Ensemble<FakeProg>)
            .GetInterfaces()
            .ShouldNotContain(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IOptimizer<>));

    [Fact]
    public void Ctor_NullReducer_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new Ensemble<FakeProg>(reducer: null!));

    [Fact]
    public async Task CompileAsync_NullPrograms_Throws()
    {
        var ensemble = new Ensemble<FakeProg>((preds, _) => preds.First());
        await Should.ThrowAsync<ArgumentNullException>(() =>
            ensemble.CompileAsync(null!, TestHelpers.MakeContext(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        var ensemble = new Ensemble<FakeProg>((preds, _) => preds.First());
        await Should.ThrowAsync<ArgumentNullException>(() =>
            ensemble.CompileAsync(new List<FakeProg> { new() }, null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_EmptyPrograms_Throws()
    {
        var ensemble = new Ensemble<FakeProg>((preds, _) => preds.First());
        await Should.ThrowAsync<ArgumentException>(() =>
            ensemble.CompileAsync(new List<FakeProg>(),
                TestHelpers.MakeContext(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_ReturnTypeIsOptimizedProgramOfEnsembleProgram()
    {
        var programs = new List<FakeProg> { new(), new() };
        var ensemble = new Ensemble<FakeProg>((preds, _) => preds.First());

        var result = await ensemble.CompileAsync(
            programs, TestHelpers.MakeContext(),
            TestContext.Current.CancellationToken);

        result.ShouldBeOfType<OptimizedProgram<EnsembleProgram<FakeProg>>>();
        result.Program.Members.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ensemble = new Ensemble<FakeProg>((preds, _) => preds.First());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ensemble.CompileAsync(
                new List<FakeProg> { new() },
                TestHelpers.MakeContext(), cts.Token));
    }

    [Fact]
    public void EnsembleProgram_StoresAllMembers()
    {
        var members = new List<FakeProg> { new(), new(), new() };
        var ep = new EnsembleProgram<FakeProg>(members, (preds, _) => preds.First());
        ep.Members.Count.ShouldBe(3);
    }

    [Fact]
    public void EnsembleProgram_NullMembers_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new EnsembleProgram<FakeProg>(null!, (preds, _) => preds.First()));

    [Fact]
    public void EnsembleProgram_NullReducer_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new EnsembleProgram<FakeProg>(new List<FakeProg> { new() }, null!));

    [Fact]
    public void EnsembleProgram_ReducerExposed()
    {
        var ep = new EnsembleProgram<FakeProg>(
            new List<FakeProg> { new() },
            (preds, _) => preds.First());
        ep.Reducer.ShouldNotBeNull();
    }

    sealed class FakeProg { }
}
