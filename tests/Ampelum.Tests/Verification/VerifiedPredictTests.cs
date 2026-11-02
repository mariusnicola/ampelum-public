using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Collection(ActivityListenerCollection.Name)]
public sealed class VerifiedPredictTests
{
    [Signature("Answer concisely.")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    static AmpelumContext Ctx(IChatClient chat, ITraceSink? sink = null, IAdapter? adapter = null) =>
        new(chat, adapter ?? new ChatAdapter(), sink ?? new InMemoryTraceSink(), NullLoggerFactory.Instance);

    [Fact]
    public async Task SchemaCriticPolicy_ApprovesOutput_WithAllFields()
    {
        var critic = new SchemaCriticPolicy<Qa>();
        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "What is 2+2?" },
            new Dictionary<string, object?> { }
        );
        var output = new Example(
            new Dictionary<string, object?> { },
            new Dictionary<string, object?> { ["Answer"] = "4" }
        );
        var ctx = Ctx(new FakeChatClient());

        var verdict = await critic.EvaluateAsync(input, output, ctx, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeTrue();
        verdict.Reasoning.ShouldContain("Schema validation passed");
    }

    [Fact]
    public async Task SchemaCriticPolicy_RejectsOutput_WithNoFields()
    {
        var critic = new SchemaCriticPolicy<Qa>();
        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "What is 2+2?" },
            new Dictionary<string, object?> { }
        );
        var output = new Example(
            new Dictionary<string, object?> { },
            new Dictionary<string, object?> { }
        );
        var ctx = Ctx(new FakeChatClient());

        var verdict = await critic.EvaluateAsync(input, output, ctx, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeFalse();
        verdict.Reasoning.ShouldContain("no output fields");
        verdict.RejectionReason.ShouldBe(CriticRejectionReason.SchemaViolation);
    }

    [Fact]
    public async Task SchemaCriticPolicy_RejectsOutput_WithNullFields()
    {
        var critic = new SchemaCriticPolicy<Qa>();
        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "What is 2+2?" },
            new Dictionary<string, object?> { }
        );
        var output = new Example(
            new Dictionary<string, object?> { },
            new Dictionary<string, object?> { ["Answer"] = null }
        );
        var ctx = Ctx(new FakeChatClient());

        var verdict = await critic.EvaluateAsync(input, output, ctx, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeFalse();
        verdict.Reasoning.ShouldContain("null output fields");
        verdict.RejectionReason.ShouldBe(CriticRejectionReason.SchemaViolation);
    }

    [Fact]
    public async Task CompositeCriticPolicy_ApprovesWhenAllApprove()
    {
        var policy1 = new SchemaCriticPolicy<Qa>();
        var policy2 = new SchemaCriticPolicy<Qa>();
        var composite = new CompositeCriticPolicy<Qa>(new[] { policy1, policy2 });

        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "?" },
            new Dictionary<string, object?> { }
        );
        var output = new Example(
            new Dictionary<string, object?> { },
            new Dictionary<string, object?> { ["Answer"] = "ok" }
        );
        var ctx = Ctx(new FakeChatClient());

        var verdict = await composite.EvaluateAsync(input, output, ctx, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeTrue();
        verdict.Reasoning.ShouldBe("All policies approved.");
    }

    [Fact]
    public async Task CompositeCriticPolicy_RejectsWhenFirstRejects()
    {
        var alwaysReject = new AlwaysRejectCritic<Qa>();
        var alwaysApprove = new SchemaCriticPolicy<Qa>();
        var composite = new CompositeCriticPolicy<Qa>(new ICriticPolicy<Qa>[] { alwaysReject, alwaysApprove });

        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "?" },
            new Dictionary<string, object?> { }
        );
        var output = new Example(
            new Dictionary<string, object?> { },
            new Dictionary<string, object?> { ["Answer"] = "ok" }
        );
        var ctx = Ctx(new FakeChatClient());

        var verdict = await composite.EvaluateAsync(input, output, ctx, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeFalse();
        verdict.Reasoning.ShouldBe("Rejected by test policy");
    }

    [Fact]
    public async Task VerifiedPredict_ReturnsOutputOnFirstApproval()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Answer ## ]]\n42");
        var ctx = Ctx(chat);
        var predict = new Predict<Qa>(ctx);
        var critic = new SchemaCriticPolicy<Qa>();
        var verified = new VerifiedPredict<Qa>(predict, critic);

        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "What is 6*7?" },
            new Dictionary<string, object?> { }
        );
        var result = await verified.PredictAsync(input, ctx, TestContext.Current.CancellationToken);

        result.Outputs["Answer"].ShouldBe("42");
    }

    [Fact]
    public async Task VerifiedPredict_ThrowsAfterExhaustedRetries_WhenThrowOnRejectionTrue()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        var ctx = Ctx(chat);
        var predict = new Predict<Qa>(ctx);
        var critic = new AlwaysRejectCritic<Qa>();
        var options = new VerifiedPredictOptions { MaxRetries = 2, ThrowOnRejection = true };
        var verified = new VerifiedPredict<Qa>(predict, critic, options);

        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "?" },
            new Dictionary<string, object?> { }
        );

        var ex = await Should.ThrowAsync<VerificationFailedException>(
            () => verified.PredictAsync(input, ctx, TestContext.Current.CancellationToken));

        ex.AttemptCount.ShouldBe(3); // 1 initial + 2 retries
        ex.LastVerdict.Approved.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifiedPredict_ReturnsLastOnExhaustion_WhenReturnLastOnExhaustionTrue()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        chat.EnqueueText("[[ ## Answer ## ]]\nfail");
        var ctx = Ctx(chat);
        var predict = new Predict<Qa>(ctx);
        var critic = new AlwaysRejectCritic<Qa>();
        var options = new VerifiedPredictOptions
        {
            MaxRetries = 2,
            ThrowOnRejection = false,
            ReturnLastOnExhaustion = true
        };
        var verified = new VerifiedPredict<Qa>(predict, critic, options);

        var input = new Example(
            new Dictionary<string, object?> { ["Question"] = "?" },
            new Dictionary<string, object?> { }
        );
        var result = await verified.PredictAsync(input, ctx, TestContext.Current.CancellationToken);

        result.Outputs.ShouldContainKey("Answer");
        result.Outputs["Answer"].ShouldBe("fail");
    }

    [Fact]
    public void VerifiedPredictOptions_Defaults()
    {
        var opts = new VerifiedPredictOptions();

        opts.MaxRetries.ShouldBe(2);
        opts.ThrowOnRejection.ShouldBeTrue();
        opts.ReturnLastOnExhaustion.ShouldBeFalse();
    }

    [Fact]
    public void CriticVerdict_CanConstruct()
    {
        var verdict = new CriticVerdict(
            Approved: true,
            Reasoning: "Test",
            RejectionReason: null,
            Suggestions: null
        );

        verdict.Approved.ShouldBeTrue();
        verdict.Reasoning.ShouldBe("Test");
    }

    [Fact]
    public void VerificationFailedException_StoresAttemptCountAndVerdict()
    {
        var verdict = new CriticVerdict(
            Approved: false,
            Reasoning: "Test failure"
        );
        var ex = new VerificationFailedException(3, verdict);

        ex.AttemptCount.ShouldBe(3);
        ex.LastVerdict.ShouldBeSameAs(verdict);
        ex.Message.ShouldContain("3 attempt(s)");
        ex.Message.ShouldContain("Test failure");
    }

    // Helper critic for testing
    sealed class AlwaysRejectCritic<TSig> : ICriticPolicy<TSig> where TSig : class
    {
        public Task<CriticVerdict> EvaluateAsync(
            Example input,
            Example generatorOutput,
            AmpelumContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(new CriticVerdict(
                Approved: false,
                Reasoning: "Rejected by test policy",
                RejectionReason: CriticRejectionReason.Custom
            ));
        }
    }
}
