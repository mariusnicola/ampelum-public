using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public class IComposedModuleTests
{
    [Fact]
    public void IComposedModule_Submodules_StableInsertionOrder()
    {
        IComposedModule cm = new TwoModuleProgram();
        var keys = cm.Submodules.Keys.ToList();
        keys[0].ShouldBe("retriever");
        keys[1].ShouldBe("answer");
    }

    [Fact]
    public void IComposedModule_Rebind_ReturnsDifferentInstance()
    {
        IComposedModule cm = new TwoModuleProgram();
        var ctx = TestHelpers.MakeContext();
        var submodules = new Dictionary<string, object>
        {
            ["retriever"] = new Predict<MinimalSig>(ctx),
            ["answer"]    = new Predict<MinimalSig>(ctx),
        };
        var rebound = cm.Rebind(submodules);
        rebound.ShouldNotBeSameAs(cm);
        rebound.ShouldBeOfType<TwoModuleProgram>();
    }

    [Fact]
    public void IComposedModule_Rebind_DoesNotMutateThis()
    {
        IComposedModule original = new TwoModuleProgram();
        var ctx = TestHelpers.MakeContext();
        var submodules = new Dictionary<string, object>
        {
            ["retriever"] = new Predict<MinimalSig>(ctx),
            ["answer"]    = new Predict<MinimalSig>(ctx),
        };
        _ = original.Rebind(submodules);
        original.Submodules.Count.ShouldBe(2);
    }

    [Signature("Minimal.")]
    public sealed record MinimalSig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class TwoModuleProgram : IComposedModule
    {
        Predict<MinimalSig> _retriever = new(TestHelpers.MakeContext());
        Predict<MinimalSig> _answer    = new(TestHelpers.MakeContext());

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>
            {
                ["retriever"] = _retriever,
                ["answer"]    = _answer,
            };

        public object Rebind(IReadOnlyDictionary<string, object> newSubmodules)
        {
            var copy = new TwoModuleProgram();
            copy._retriever = (Predict<MinimalSig>)newSubmodules["retriever"];
            copy._answer    = (Predict<MinimalSig>)newSubmodules["answer"];
            return copy;
        }
    }
}
