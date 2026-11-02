using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class LlmSkillSelectorTests
{
    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = [];
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    private static SkillManifest ASkill(string name, string[] tags) =>
        new()
        {
            Name = name,
            Version = "1.0.0",
            Tags = tags
        };

    [Fact]
    public async Task Select_WithFake_ReturnsTopMatch()
    {
        // Arrange
        var fake = new FakeChatClient();
        fake.EnqueueText("""{"selected": ["test-skill"], "reasoning": "Best match"}""");

        var skills = new[]
        {
            ASkill("test-skill", ["code"]),
            ASkill("other-skill", ["docs"])
        };
        var selector = new LlmSkillSelector(fake, taskDescription: "write code");

        // Act
        var result = await selector.SelectAsync(skills, ["code"], TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("test-skill");
    }

    [Fact]
    public async Task Select_FakeReturnsEmpty_ReturnsAll()
    {
        // Arrange
        var fake = new FakeChatClient();
        // LLM returns selected names that don't match any skill → fallback to tag matching
        fake.EnqueueText("""{"selected": ["bogus-skill"], "reasoning": "No clue"}""");

        var skills = new[]
        {
            ASkill("csharp-skill", ["csharp"]),
            ASkill("python-skill", ["python"])
        };
        var selector = new LlmSkillSelector(fake, taskDescription: "write csharp");

        // Act
        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        // Assert — falls back to tag matching
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("csharp-skill");
    }

    [Fact]
    public async Task Select_TagFallback_WhenNoLlm()
    {
        // Arrange
        var skills = new[]
        {
            ASkill("csharp-skill", ["csharp"]),
            ASkill("python-skill", ["python"])
        };
        var selector = new LlmSkillSelector(); // no IChatClient → tag fallback

        // Act
        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("csharp-skill");
    }

    [Fact]
    public async Task Select_HandlesInvalidJson()
    {
        // Arrange
        var fake = new FakeChatClient();
        fake.EnqueueText("This is not valid JSON at all");

        var skills = new[]
        {
            ASkill("csharp-skill", ["csharp"]),
            ASkill("python-skill", ["python"])
        };
        var selector = new LlmSkillSelector(fake, taskDescription: "write csharp");

        // Act
        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        // Assert — gracefully handles invalid JSON, returns empty or falls back
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Select_WithAmbientContext_RunsHooksAndUsesInjectedChat_NotAmbientChat()
    {
        // Arrange
        var injectedChat = new FakeChatClient();
        injectedChat.EnqueueText("""{"selected": ["test-skill"], "reasoning": "Best match"}""");
        var ambientChat = new FakeChatClient(); // nothing enqueued — must not be called
        var hook = new SpyHook();
        var ambientContext = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        var skills = new[]
        {
            ASkill("test-skill", ["code"]),
            ASkill("other-skill", ["docs"])
        };
        var selector = new LlmSkillSelector(injectedChat, taskDescription: "write code");

        // Act
        using var _ = AmpelumContext.Push(ambientContext);
        var result = await selector.SelectAsync(skills, ["code"], TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("test-skill");
        injectedChat.RecordedCalls.Count.ShouldBe(1);
        ambientChat.RecordedCalls.ShouldBeEmpty();
        hook.Calls.ShouldBe(["before:LlmSkillSelector.Select", "after:LlmSkillSelector.Select"]);
    }

    [Fact]
    public void PromptTemplate_IsNonEmpty()
    {
        // Assert
        LlmSkillSelector.PromptTemplate.ShouldNotBeNullOrWhiteSpace();
    }
}
