namespace OrdersApi.Domain;

/// <param name="ThresholdMinor">Smallest subtotal (inclusive, minor units) that earns this tier.</param>
/// <param name="Percent">Whole-number percentage taken off the subtotal.</param>
public sealed record DiscountTier(long ThresholdMinor, int Percent);

public sealed record Quote(long SubtotalMinor, int DiscountPercent, long DiscountMinor, long TotalMinor);

/// <summary>
/// Money is always an integer number of minor units (cents). The discount is a percentage of the whole subtotal
/// chosen by the highest tier whose threshold the subtotal reaches (threshold inclusive). It is rounded HALF UP
/// to the nearest minor unit: discount = floor((subtotal * percent + 50) / 100). Amounts are never negative,
/// so half-up and half-away-from-zero are the same thing. Arithmetic is checked: overflow is an error, never a
/// silently wrong total.
/// </summary>
public sealed class PricingPolicy
{
    // 5% from 100.00, 10% from 500.00, 15% from 1000.00 (minor units).
    public static readonly PricingPolicy Default = new(
    [
        new DiscountTier(10_000, 5),
        new DiscountTier(50_000, 10),
        new DiscountTier(100_000, 15),
    ]);

    private readonly DiscountTier[] _tiersHighestFirst;

    public PricingPolicy(IEnumerable<DiscountTier> tiers)
    {
        _tiersHighestFirst = tiers.OrderByDescending(t => t.ThresholdMinor).ToArray();
    }

    public static long LineTotal(long unitPriceMinor, int quantity)
    {
        try
        {
            return checked(unitPriceMinor * quantity);
        }
        catch (OverflowException)
        {
            throw new DomainException("amount-too-large", "The line total does not fit in a 64-bit amount.");
        }
    }

    public int PercentFor(long subtotalMinor)
    {
        foreach (var tier in _tiersHighestFirst)
        {
            if (subtotalMinor >= tier.ThresholdMinor)
            {
                return tier.Percent;
            }
        }

        return 0;
    }

    public static long DiscountFor(long subtotalMinor, int percent)
    {
        try
        {
            return checked(subtotalMinor * percent + 50) / 100;
        }
        catch (OverflowException)
        {
            throw new DomainException("amount-too-large", "The discount does not fit in a 64-bit amount.");
        }
    }

    public Quote Quote(IEnumerable<OrderLine> lines)
    {
        long subtotal = 0;
        foreach (var line in lines)
        {
            try
            {
                subtotal = checked(subtotal + line.LineTotalMinor);
            }
            catch (OverflowException)
            {
                throw new DomainException("amount-too-large", "The order subtotal does not fit in a 64-bit amount.");
            }
        }

        var percent = PercentFor(subtotal);
        var discount = DiscountFor(subtotal, percent);
        return new Quote(subtotal, percent, discount, subtotal - discount);
    }
}
