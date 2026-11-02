#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class ModelConfigTests : IDisposable
{
    private static readonly string[] Vars =
        ["OPENROUTER_MODEL_1", "OPENROUTER_MODEL_2", "LMSTUDIO_MODEL", "OLLAMA_MODEL"];

    private readonly Dictionary<string, string?> _saved = [];

    public ModelConfigTests()
    {
        foreach (var v in Vars)
        {
            _saved[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void DefaultModel_Unset_ThrowsNamingVariableAndSampleEnv()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ModelConfig.DefaultModel);
        ex.Message.ShouldContain("OPENROUTER_MODEL_1");
        ex.Message.ShouldContain(".sample.env");
    }

    [Fact]
    public void TestModel_Unset_Throws() =>
        Should.Throw<InvalidOperationException>(() => ModelConfig.TestModel).Message.ShouldContain(".sample.env");

    [Fact]
    public void LmStudioModel_Unset_Throws() =>
        Should.Throw<InvalidOperationException>(() => ModelConfig.LmStudioModel).Message.ShouldContain("LMSTUDIO_MODEL");

    [Fact]
    public void OllamaModel_Unset_Throws() =>
        Should.Throw<InvalidOperationException>(() => ModelConfig.OllamaModel).Message.ShouldContain("OLLAMA_MODEL");

    [Fact]
    public void DefaultModel_Set_ReturnsValue()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_MODEL_1", "m1");
        ModelConfig.DefaultModel.ShouldBe("m1");
        ModelConfig.TestModel.ShouldBe("m1");
        Environment.SetEnvironmentVariable("OPENROUTER_MODEL_2", "m2");
        ModelConfig.TestModel.ShouldBe("m2");
    }
}
