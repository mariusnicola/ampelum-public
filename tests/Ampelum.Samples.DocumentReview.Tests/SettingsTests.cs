using Ampelum.Samples.DocumentReview;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("cohere", "Cohere")]
    [InlineData("Voyage", "Voyage")]
    public void Reranker_ReadsTheChoiceAndItsKey(string raw, string expected)
    {
        var settings = Rig.SettingsOf((ReviewSettings.RerankerName, raw), (ReviewSettings.RerankerKeyName, "k"), (ReviewSettings.RerankerModelName, "m"));

        var (kind, key, model) = settings.Reranker();

        kind.ToString().ShouldBe(expected);
        key.ShouldBe("k");
        model.ShouldBe("m");
    }

    [Fact]
    public void Reranker_MissingModel_NamesTheModelSetting()
    {
        var settings = Rig.SettingsOf((ReviewSettings.RerankerName, "cohere"), (ReviewSettings.RerankerKeyName, "k"));

        Should.Throw<InvalidOperationException>(() => settings.Reranker()).Message.ShouldContain(ReviewSettings.RerankerModelName);
    }

    [Fact]
    public void Reranker_UnknownChoice_NamesTheSetting()
    {
        var settings = Rig.SettingsOf((ReviewSettings.RerankerName, "other"), (ReviewSettings.RerankerKeyName, "k"));

        Should.Throw<InvalidOperationException>(() => settings.Reranker()).Message.ShouldContain(ReviewSettings.RerankerName);
    }

    [Fact]
    public void Reranker_MissingKey_NamesTheKeySetting()
    {
        var settings = Rig.SettingsOf((ReviewSettings.RerankerName, "voyage"));

        Should.Throw<InvalidOperationException>(() => settings.Reranker()).Message.ShouldContain(ReviewSettings.RerankerKeyName);
    }

    [Fact]
    public void EachPartReadsOnlyItsOwnNames()
    {
        var batchOnly = Rig.SettingsOf((ReviewSettings.BatchKeyName, "k"), (ReviewSettings.BatchModelName, "m"));
        batchOnly.Batch().ShouldBe(("k", "m"));
        Should.Throw<InvalidOperationException>(() => batchOnly.Reranker()).Message.ShouldContain(ReviewSettings.RerankerName);
        Should.Throw<InvalidOperationException>(() => batchOnly.AgentUrl()).Message.ShouldContain(ReviewSettings.AgentUrlName);

        var agentOnly = Rig.SettingsOf((ReviewSettings.AgentUrlName, "http://127.0.0.1:1/"));
        agentOnly.AgentUrl().Port.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => agentOnly.Batch()).Message.ShouldContain(ReviewSettings.BatchKeyName);
    }

    [Fact]
    public void Batch_MissingModel_DoesNotFallBackToAnotherSetting()
    {
        var settings = Rig.SettingsOf((ReviewSettings.BatchKeyName, "k"), (ReviewSettings.RerankerKeyName, "other"));

        Should.Throw<InvalidOperationException>(() => settings.Batch()).Message.ShouldContain(ReviewSettings.BatchModelName);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1/")]
    public void AgentUrl_MustBeHttp(string raw)
    {
        var settings = Rig.SettingsOf((ReviewSettings.AgentUrlName, raw));

        Should.Throw<InvalidOperationException>(() => settings.AgentUrl()).Message.ShouldContain(ReviewSettings.AgentUrlName);
    }
}
