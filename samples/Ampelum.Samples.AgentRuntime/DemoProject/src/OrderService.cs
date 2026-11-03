namespace Shop;

public sealed class OrderService
{
    private readonly Pricing _pricing = new();

    public decimal Total(IReadOnlyList<decimal> lines, decimal discountPercent)
    {
        dynamic sum = 0m;
        foreach (var line in lines)
            sum += line;
        return _pricing.ApplyDiscount(sum, discountPercent);
    }
}
