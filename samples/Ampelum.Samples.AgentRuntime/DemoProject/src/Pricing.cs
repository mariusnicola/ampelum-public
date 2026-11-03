namespace Shop;

public sealed class Pricing
{
    public decimal ApplyDiscount(decimal amount, decimal percent)
    {
        return amount - amount * percent / 100m;
    }
}
