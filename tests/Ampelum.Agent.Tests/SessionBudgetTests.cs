using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SessionBudgetTests
{
    [Fact]
    public void IsWarning_UnderAllLimits_ReturnsFalse()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsWarning(5, 1000, 500);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsWarning_CallCountAbove80Percent_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsWarning(85, 1000, 500);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWarning_TokensAbove80Percent_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsWarning(5, 85_000, 500);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWarning_OutputAbove80Percent_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsWarning(5, 1000, 900_000);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWarning_ExhaustedCallCount_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsWarning(101, 1000, 500);

        result.ShouldBeTrue(); // exceeded also qualifies as warning
    }

    [Fact]
    public void IsExhausted_ExceedsCallLimit_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsExhausted(101, 1000, 500);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsExhausted_ExceedsTokenLimit_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsExhausted(5, 100_001, 500);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsExhausted_ExceedsOutputBytes_ReturnsTrue()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsExhausted(5, 1000, 1_000_001);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsExhausted_WithinLimits_ReturnsFalse()
    {
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30)
        };

        var result = budget.IsExhausted(50, 50_000, 500_000);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsWarning_NullLimits_ReturnsFalse()
    {
        var budget = new SessionBudget(); // all null

        var result = budget.IsWarning(1_000_000, 1_000_000, 1_000_000);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsExhausted_NullLimits_ReturnsFalse()
    {
        var budget = new SessionBudget(); // all null

        var result = budget.IsExhausted(1_000_000, 1_000_000, 1_000_000);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsWarning_DurationUsesStartedAt_ReturnsCorrectResult()
    {
        // StartedAt defaults to UtcNow; for fixed 80% test use StartedAt = now - 25 min (30 min budget → 83%)
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-25);
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30),
            StartedAt = startedAt
        };

        // At 25 min of elapsed time for a 30 min budget → 83% > 80% → warning
        var result = budget.IsWarning(5, 1000, 500);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsExhausted_DurationUsesStartedAt_ReturnsCorrectResult()
    {
        // 31 min elapsed for 30 min budget → 103% → exhausted
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-31);
        var budget = new SessionBudget
        {
            MaxCallCount = 100,
            MaxTokens = 100_000,
            MaxOutputBytes = 1_000_000,
            MaxDuration = TimeSpan.FromMinutes(30),
            StartedAt = startedAt
        };

        var result = budget.IsExhausted(5, 1000, 500);

        result.ShouldBeTrue();
    }
}