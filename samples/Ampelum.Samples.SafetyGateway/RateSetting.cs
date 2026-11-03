using System.Globalization;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>Reads a per-token price from a setting. Exponent notation such as 2e-7 is accepted because token prices are tiny.</summary>
public static class RateSetting
{
    public static decimal Parse(string value) =>
        decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
