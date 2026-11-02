using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Routing;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RoutingPrimitivesTests
{
    static ProviderEndpoint Endpoint(string name) =>
        new(name, new FakeChatClient().EnqueueText("[[ ## A ## ]]\nok"));

    // ── ProviderEndpoint ─────────────────────────────────────────────────────

    [Fact]
    public void ProviderEndpoint_DefaultWeight_IsOne()
    {
        var e = Endpoint("a");
        e.Name.ShouldBe("a");
        e.Weight.ShouldBe(1.0);
    }

    [Fact]
    public void ProviderEndpoint_CustomWeight_Round_Trips()
    {
        var inner = new FakeChatClient();
        var e = new ProviderEndpoint("a", inner, 2.5);
        e.Weight.ShouldBe(2.5);
        e.Client.ShouldBeSameAs(inner);
    }

    // ── RoutingDecision ──────────────────────────────────────────────────────

    [Fact]
    public void RoutingDecision_HoldsPrimaryAndFallbacks()
    {
        var p = Endpoint("p");
        var f = Endpoint("f");
        var decision = new RoutingDecision(p, [f]);
        decision.Primary.ShouldBe(p);
        decision.Fallbacks.ShouldHaveSingleItem().ShouldBe(f);
    }

    // ── Failover strategy ────────────────────────────────────────────────────

    [Fact]
    public void Failover_PicksFirstAndExposesRestAsFallback()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var c = Endpoint("c");
        var decision = new Failover().Select([a, b, c]);
        decision.Primary.ShouldBe(a);
        decision.Fallbacks.Count.ShouldBe(2);
        decision.Fallbacks[0].ShouldBe(b);
        decision.Fallbacks[1].ShouldBe(c);
    }

    [Fact]
    public void Failover_SingleEndpoint_NoFallbacks()
    {
        var only = Endpoint("only");
        var decision = new Failover().Select([only]);
        decision.Primary.ShouldBe(only);
        decision.Fallbacks.ShouldBeEmpty();
    }

    [Fact]
    public void Failover_NameIsStable() => new Failover().Name.ShouldBe("failover");

    [Fact]
    public void Failover_NullEndpoints_Throws() =>
        Should.Throw<ArgumentNullException>(() => new Failover().Select(null!));

    [Fact]
    public void Failover_EmptyEndpoints_Throws() =>
        Should.Throw<ArgumentException>(() => new Failover().Select(Array.Empty<ProviderEndpoint>()));

    // ── LoadBalanced (round-robin) ───────────────────────────────────────────

    [Fact]
    public void LoadBalanced_RotatesAcrossEndpoints()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var c = Endpoint("c");
        var lb = new LoadBalanced();
        lb.Select([a, b, c]).Primary.Name.ShouldBe("a");
        lb.Select([a, b, c]).Primary.Name.ShouldBe("b");
        lb.Select([a, b, c]).Primary.Name.ShouldBe("c");
        lb.Select([a, b, c]).Primary.Name.ShouldBe("a"); // wraps
    }

    [Fact]
    public void LoadBalanced_ExcludesPrimaryFromFallbacks()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var lb = new LoadBalanced();
        var first = lb.Select([a, b]);
        first.Fallbacks.ShouldHaveSingleItem().Name.ShouldBe("b");
        var second = lb.Select([a, b]);
        second.Primary.Name.ShouldBe("b");
        second.Fallbacks.ShouldHaveSingleItem().Name.ShouldBe("a");
    }

    [Fact]
    public void LoadBalanced_SingleEndpoint_NoFallbacks()
    {
        var only = Endpoint("only");
        new LoadBalanced().Select([only]).Fallbacks.ShouldBeEmpty();
    }

    [Fact]
    public void LoadBalanced_Name_IsStable() => new LoadBalanced().Name.ShouldBe("load-balanced");

    [Fact]
    public void LoadBalanced_NullEndpoints_Throws() =>
        Should.Throw<ArgumentNullException>(() => new LoadBalanced().Select(null!));

    [Fact]
    public void LoadBalanced_EmptyEndpoints_Throws() =>
        Should.Throw<ArgumentException>(() => new LoadBalanced().Select(Array.Empty<ProviderEndpoint>()));

    // ── KeyRotating ──────────────────────────────────────────────────────────

    [Fact]
    public void KeyRotating_DefaultRotatesEveryCall()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var kr = new KeyRotating();
        kr.Select([a, b]).Primary.Name.ShouldBe("a");
        kr.Select([a, b]).Primary.Name.ShouldBe("b");
        kr.Select([a, b]).Primary.Name.ShouldBe("a");
    }

    [Fact]
    public void KeyRotating_CallsPerKey_Buckets()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var kr = new KeyRotating(callsPerKey: 2);
        kr.Select([a, b]).Primary.Name.ShouldBe("a");
        kr.Select([a, b]).Primary.Name.ShouldBe("a");
        kr.Select([a, b]).Primary.Name.ShouldBe("b");
        kr.Select([a, b]).Primary.Name.ShouldBe("b");
        kr.Select([a, b]).Primary.Name.ShouldBe("a");
    }

    [Fact]
    public void KeyRotating_FallbacksAreOtherEndpoints()
    {
        var a = Endpoint("a");
        var b = Endpoint("b");
        var c = Endpoint("c");
        var kr = new KeyRotating();
        var decision = kr.Select([a, b, c]); // first → a
        decision.Primary.Name.ShouldBe("a");
        decision.Fallbacks.Select(f => f.Name).ShouldBe(new[] { "b", "c" });
    }

    [Fact]
    public void KeyRotating_SingleEndpoint_NoFallbacks() =>
        new KeyRotating().Select([Endpoint("solo")]).Fallbacks.ShouldBeEmpty();

    [Fact]
    public void KeyRotating_Name_IsStable() => new KeyRotating().Name.ShouldBe("key-rotating");

    [Fact]
    public void KeyRotating_InvalidCallsPerKey_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new KeyRotating(callsPerKey: 0));

    [Fact]
    public void KeyRotating_NullEndpoints_Throws() =>
        Should.Throw<ArgumentNullException>(() => new KeyRotating().Select(null!));

    [Fact]
    public void KeyRotating_EmptyEndpoints_Throws() =>
        Should.Throw<ArgumentException>(() => new KeyRotating().Select(Array.Empty<ProviderEndpoint>()));
}
