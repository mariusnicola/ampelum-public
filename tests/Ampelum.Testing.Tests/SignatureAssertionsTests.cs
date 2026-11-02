using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

public sealed class SignatureAssertionsTests
{
    [Signature("")]
    sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public void ShouldBeValidSignature_WellFormed_Passes() =>
        SignatureAssertions.ShouldBeValidSignature<Qa>();

    [Fact]
    public void ShouldHaveInput_Existing_Passes() =>
        SignatureAssertions.ShouldHaveInput<Qa>("Question");

    [Fact]
    public void ShouldHaveInput_Missing_Throws() =>
        Should.Throw<SignatureAssertionException>(() => SignatureAssertions.ShouldHaveInput<Qa>("Nope"))
              .Message.ShouldContain("Nope");

    [Fact]
    public void ShouldHaveOutput_Existing_Passes() =>
        SignatureAssertions.ShouldHaveOutput<Qa>("Answer");

    [Fact]
    public void ShouldHaveOutput_Missing_Throws() =>
        Should.Throw<SignatureAssertionException>(() => SignatureAssertions.ShouldHaveOutput<Qa>("Nope"))
              .Message.ShouldContain("Nope");
}
