namespace Company.Enterprise.Budget;

/// <summary>Token / cost budget configuration for the enterprise template.</summary>
public static class BudgetConfiguration
{
    public static void Configure(global::Ampelum.Budget.BudgetOptions options)
    {
        // Cap at 1M tokens / day by default — adjust per environment.
        options.MaxTokensPerDay = 1_000_000;
        options.MaxRequestsPerMinute = 600;
    }
}
