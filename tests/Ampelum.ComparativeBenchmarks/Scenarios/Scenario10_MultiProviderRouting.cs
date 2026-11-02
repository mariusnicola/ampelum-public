using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using SignalGrid.Ampelum.Routing;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S10 — multi-provider routing throughput.
///
/// Plan name: Scenario10_MultiProviderRouting (must-be-#1 per §B3 — first/only).
///
/// Compares <see cref="RoutedChatClient"/> with <see cref="LoadBalanced"/>
/// (round-robin) strategy against a hand-rolled round-robin loop over the same
/// set of <see cref="MockChatClient"/> instances. The hand-rolled baseline is
/// the lower-bound any user could write themselves; Ampelum has to stay close.
///
/// <para>No SK / AutoGen / LangChain row: none of the pinned competitors ship a
/// provider-router decorator over <see cref="IChatClient"/>; the ranker emits
/// "—" in those columns. Honest reporting per the alpha.6 brief.</para>
///
/// <para>Streaming is excluded — <see cref="RoutedChatClient"/> intentionally pins
/// streams to the primary endpoint (mid-stream retry on a fallback is unsafe);
/// the streaming row stays in S04.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario10_MultiProviderRouting
{
    const int EndpointCount = 4;

    IChatClient[] _rawClients = null!;
    RoutedChatClient _routed = null!;
    long _handCursor = -1;
    static readonly ChatMessage[] _msg = [new ChatMessage(ChatRole.User, "q")];

    [GlobalSetup]
    public void Setup()
    {
        _rawClients = new IChatClient[EndpointCount];
        var endpoints = new ProviderEndpoint[EndpointCount];
        for (var i = 0; i < EndpointCount; i++)
        {
            var c = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
            _rawClients[i] = c;
            endpoints[i] = new ProviderEndpoint($"p{i}", c);
        }
        _routed = new RoutedChatClient(endpoints, new LoadBalanced());
    }

    [Benchmark(Baseline = true)]
    public async Task<ChatResponse> Ampelum_Routed()
        => await _routed.GetResponseAsync(_msg);

    /// <summary>Hand-coded round-robin loop — what a user writes without Ampelum.
    /// Strictly minimal: increment a counter, pick a client, dispatch. No
    /// audit-stamp metadata, no fallback chain — this is the floor.</summary>
    [Benchmark]
    public async Task<ChatResponse> HandRolled_RoundRobin()
    {
        var next = Interlocked.Increment(ref _handCursor);
        var idx = (int)((ulong)next % (ulong)_rawClients.Length);
        return await _rawClients[idx].GetResponseAsync(_msg);
    }
}
