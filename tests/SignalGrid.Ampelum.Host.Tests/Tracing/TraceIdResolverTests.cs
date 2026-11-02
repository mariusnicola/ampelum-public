#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Tests.Tracing;

public sealed class TraceIdResolverTests
{
    [Fact]
    public void Resolve_WithValidParentTraceId_ReturnsIt()
    {
        var input = "0123456789abcdef0123456789abcdef";
        var result = TraceIdResolver.Resolve(input);
        result.ShouldBe(input);
    }

    [Fact]
    public void Resolve_WithNull_Returns32LowercaseHex()
    {
        var result = TraceIdResolver.Resolve(null);
        result.Length.ShouldBe(32);
        result.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void Resolve_WithEmpty_Returns32LowercaseHex()
    {
        var result = TraceIdResolver.Resolve("");
        result.Length.ShouldBe(32);
        result.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void Resolve_WithUpperCase_IgnoresAndMints()
    {
        var input = "ABCDEF0123456789ABCDEF0123456789";
        var result = TraceIdResolver.Resolve(input);
        result.ShouldNotBe(input);
        result.Length.ShouldBe(32);
        result.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void Resolve_WithWrongLength_IgnoresAndMints()
    {
        var input = "abc";
        var result = TraceIdResolver.Resolve(input);
        result.ShouldNotBe(input);
        result.Length.ShouldBe(32);
        result.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void Resolve_WithNonHexChar_IgnoresAndMints()
    {
        var input = "0123456789abcdef0123456789abcdefg";
        var result = TraceIdResolver.Resolve(input);
        result.ShouldNotBe(input);
        result.Length.ShouldBe(32);
        result.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
}
