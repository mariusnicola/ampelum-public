using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

/// <summary>Semantic cache through the real Predict and the DI registration.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheSignatureAndEmbeddingTests
{
    [Signature("Answer the question.")]
    public sealed record First : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("Answer the question.")]
    public sealed record Second : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    static readonly IReadOnlyDictionary<string, float[]> Vectors = new Dictionary<string, float[]>
    {
        // Same direction: a reworded question.
        ["Question\u001f" + "Do you ship to Germany?"] = [1f, 0f, 0f],
        ["Question\u001f" + "do you deliver to germany"] = [0.98f, 0.1f, 0f],
        // Orthogonal: a different question.
        ["Question\u001f" + "What is your return policy?"] = [0f, 1f, 0f],
    };

    static ServiceProvider Build(FakeChatClient chat, bool withEmbeddings)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IChatClient>(chat);
        sc.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var builder = sc.AddAmpelum();
        builder.AddSemanticCache();
        if (withEmbeddings)
            builder.WithEmbeddingGenerator(_ => new FakeEmbeddingGenerator(Vectors, 3));
        return sc.BuildServiceProvider();
    }

    static string Reply(string a) => "[[ ## Answer ## ]]\n" + a + "\n[[ ## completed ## ]]";

    [Fact]
    public async Task Two_signatures_with_the_same_input_names_do_not_share_an_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("first")).EnqueueText(Reply("second"));
        using var sp = Build(chat, withEmbeddings: false);
        var ctx = sp.GetRequiredService<AmpelumContext>();

        var a = await new Predict<First>(ctx).ForwardAsync(new First { Question = "Same words" }, ct: ct);
        var b = await new Predict<Second>(ctx).ForwardAsync(new Second { Question = "Same words" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(2);
        a.Value.Answer.ShouldBe("first");
        b.Value.Answer.ShouldBe("second");
    }

    [Fact]
    public async Task Same_signature_same_inputs_still_hits_without_an_embedding_generator()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("first")).EnqueueText(Reply("second"));
        using var sp = Build(chat, withEmbeddings: false);
        var predict = new Predict<First>(sp.GetRequiredService<AmpelumContext>());

        await predict.ForwardAsync(new First { Question = "Same words" }, ct: ct);
        var again = await predict.ForwardAsync(new First { Question = "Same words" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(1);
        again.Value.Answer.ShouldBe("first");
    }

    [Fact]
    public async Task Without_an_embedding_generator_a_reworded_question_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("yes")).EnqueueText(Reply("yes, we do"));
        using var sp = Build(chat, withEmbeddings: false);
        var predict = new Predict<First>(sp.GetRequiredService<AmpelumContext>());

        await predict.ForwardAsync(new First { Question = "Do you ship to Germany?" }, ct: ct);
        var reworded = await predict.ForwardAsync(new First { Question = "do you deliver to germany" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(2);
        reworded.Value.Answer.ShouldBe("yes, we do");
    }

    [Fact]
    public async Task With_an_embedding_generator_a_reworded_question_hits()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("yes")).EnqueueText(Reply("yes, we do"));
        using var sp = Build(chat, withEmbeddings: true);
        var predict = new Predict<First>(sp.GetRequiredService<AmpelumContext>());

        await predict.ForwardAsync(new First { Question = "Do you ship to Germany?" }, ct: ct);
        var reworded = await predict.ForwardAsync(new First { Question = "do you deliver to germany" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(1);
        reworded.Value.Answer.ShouldBe("yes");
    }

    [Fact]
    public async Task With_an_embedding_generator_a_different_question_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("yes")).EnqueueText(Reply("30 days"));
        using var sp = Build(chat, withEmbeddings: true);
        var predict = new Predict<First>(sp.GetRequiredService<AmpelumContext>());

        await predict.ForwardAsync(new First { Question = "Do you ship to Germany?" }, ct: ct);
        var other = await predict.ForwardAsync(new First { Question = "What is your return policy?" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(2);
        other.Value.Answer.ShouldBe("30 days");
    }

    [Fact]
    public async Task A_similar_hit_records_its_similarity()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new SemanticCacheHook(new InMemorySemanticCache(), new FakeEmbeddingGenerator(Vectors, 3));
        var first = new SignalGrid.Ampelum.Hooks.HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?> { ["Question"] = "Do you ship to Germany?" },
            Outputs = new Dictionary<string, object?> { ["Answer"] = "yes" },
        };
        await hook.OnAfterAsync(first, ct);
        var second = new SignalGrid.Ampelum.Hooks.HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?> { ["Question"] = "do you deliver to germany" },
        };

        await hook.OnBeforeAsync(second, ct);

        second.Outputs.ShouldNotBeNull();
        ((double)second.Metadata[SignalGrid.Ampelum.HookMetadataKeys.SemanticCacheSimilarity]!).ShouldBeInRange(0.95, 1.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    public void MinSimilarity_outside_zero_to_one_is_refused(double value) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new SemanticCacheHook(new InMemorySemanticCache(), null, value));

    [Fact]
    public async Task A_similar_question_under_another_signature_does_not_hit()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText(Reply("first")).EnqueueText(Reply("second"));
        using var sp = Build(chat, withEmbeddings: true);
        var ctx = sp.GetRequiredService<AmpelumContext>();

        await new Predict<First>(ctx).ForwardAsync(new First { Question = "Do you ship to Germany?" }, ct: ct);
        var b = await new Predict<Second>(ctx).ForwardAsync(new Second { Question = "do you deliver to germany" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(2);
        b.Value.Answer.ShouldBe("second");
    }
}
