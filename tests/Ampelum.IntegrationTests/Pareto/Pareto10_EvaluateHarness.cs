using System.Runtime.CompilerServices;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #10 (master plan Â§6.H, alpha.2). <see cref="Evaluate{TSig}"/> harness
/// over a 5-example dataset with a custom <see cref="IMetric{TSig}"/>. Asserts:
/// per-example pass/fail aggregates, mean score matches expected, and parallelism caps are
/// respected (peak observed concurrency â‰¤ <c>maxDegreeOfParallelism</c>).
/// </summary>
public sealed class Pareto10_EvaluateHarness : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto10_EvaluateHarness(ParetoTestFixture fx) => _fx = fx;

    /// <summary>5 examples, 3 correct + 2 incorrect; Evaluate aggregates Mean=0.6, FailureCount=0;
    /// observed peak parallel-in-flight â‰¤ MaxDegreeOfParallelism.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task Evaluate_FiveExamples_CustomMetric_AggregatesAndCapsParallelism()
    {
        var ct = TestContext.Current.CancellationToken;
        const int maxDop = 2;

        // Questionâ†’answer table: 3 correct, 2 wrong â†’ Mean=0.6. Keyed lookup makes the fake
        // immune to dequeue races under parallel evaluation.
        using var fake = new KeyedChatClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Capital of France?"]   = "Paris",     // correct
            ["Capital of Spain?"]    = "Madrid",    // correct
            ["Capital of Italy?"]    = "Florence",  // wrong (truth: Rome)
            ["Capital of Germany?"]  = "Berlin",    // correct
            ["Capital of Portugal?"] = "Madrid",    // wrong (truth: Lisbon)
        });

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        var predict = new Predict<QaSig>(ctx);

        var dataset = new List<LabeledExample<QaSig>>
        {
            new(new QaSig { Question = "Capital of France?" },   new QaSig { Question = "Capital of France?",   Answer = "Paris"  }),
            new(new QaSig { Question = "Capital of Spain?" },    new QaSig { Question = "Capital of Spain?",    Answer = "Madrid" }),
            new(new QaSig { Question = "Capital of Italy?" },    new QaSig { Question = "Capital of Italy?",    Answer = "Rome"   }),
            new(new QaSig { Question = "Capital of Germany?" },  new QaSig { Question = "Capital of Germany?",  Answer = "Berlin" }),
            new(new QaSig { Question = "Capital of Portugal?" }, new QaSig { Question = "Capital of Portugal?", Answer = "Lisbon" }),
        };

        var concurrencyProbe = new ConcurrencyProbe();
        var metric = new CaseInsensitiveAnswerMetric();

        Func<QaSig, CancellationToken, Task<Prediction<QaSig>>> program = async (input, t) =>
        {
            using var _ = concurrencyProbe.Enter();
            // Slight stall so multiple invocations overlap and the probe captures real peak.
            await Task.Delay(10, t);
            return await predict.ForwardAsync(input, ct: t);
        };

        var evaluator = new Evaluate<QaSig>(program, metric, maxDegreeOfParallelism: maxDop);
        var result = await evaluator.RunAsync(dataset, ct);

        result.Scores.Count.ShouldBe(5);
        result.Mean.ShouldBe(0.6, 1e-9);
        result.FailureCount.ShouldBe(0); // none of the program calls threw
        result.Scores.Count(s => s.Score == 1.0).ShouldBe(3);
        result.Scores.Count(s => s.Score == 0.0).ShouldBe(2);

        concurrencyProbe.Peak.ShouldBeLessThanOrEqualTo(maxDop);
    }

    /// <summary>IChatClient whose response is selected by substring-matching the user's last message
    /// against a questionâ†’answer table. Race-free under any DOP.</summary>
    sealed class KeyedChatClient : IChatClient
    {
        readonly IReadOnlyDictionary<string, string> _table;
        public KeyedChatClient(IReadOnlyDictionary<string, string> table) => _table = table;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var combined = string.Join("\n", messages.Select(m => m.Text));
            string answer = "<unmatched>";
            foreach (var (question, ans) in _table)
            {
                if (combined.Contains(question, StringComparison.OrdinalIgnoreCase))
                {
                    answer = ans;
                    break;
                }
            }
            var text = $"[[ ## Answer ## ]]\n{answer}\n[[ ## completed ## ]]";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resp = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Case-insensitive Answer-field match: 1.0 on equality, 0.0 otherwise.</summary>
    sealed class CaseInsensitiveAnswerMetric : IMetric<QaSig>
    {
        public double Score(QaSig actual, QaSig expected, SignatureMetadata signature) =>
            string.Equals(actual.Answer?.Trim(), expected.Answer?.Trim(),
                StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    /// <summary>Reentrant counter that records peak in-flight concurrency over its lifetime.</summary>
    sealed class ConcurrencyProbe
    {
        int _inFlight;
        int _peak;
        public int Peak => Volatile.Read(ref _peak);

        public IDisposable Enter()
        {
            var current = Interlocked.Increment(ref _inFlight);
            int observed;
            do
            {
                observed = Volatile.Read(ref _peak);
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref _peak, current, observed) != observed);
            return new Leaving(this);
        }

        sealed class Leaving : IDisposable
        {
            readonly ConcurrencyProbe _p;
            public Leaving(ConcurrencyProbe p) => _p = p;
            public void Dispose() => Interlocked.Decrement(ref _p._inFlight);
        }
    }
}
