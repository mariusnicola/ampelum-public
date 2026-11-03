using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class SignatureTests
{
    [Fact]
    public void Signatures_DeclareTheFieldsThePipelineFills()
    {
        SignatureAssertions.ShouldBeValidSignature<PolicyAnswerSignature>();
        SignatureAssertions.ShouldHaveInput<PolicyAnswerSignature>("Question");
        SignatureAssertions.ShouldHaveOutput<PolicyAnswerSignature>("Answer");
        SignatureAssertions.ShouldBeValidSignature<ToolAnswerSignature>();
        SignatureAssertions.ShouldHaveOutput<ToolAnswerSignature>("Answer");
    }
}
