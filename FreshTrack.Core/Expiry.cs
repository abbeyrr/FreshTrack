namespace FreshTrack.Core;

// Different storage groups need different warning windows. The dashboard invokes
// the same virtual method on both policies, making this a useful polymorphic rule.
public abstract class ExpiryPolicy
{
    public abstract int WarningDays { get; }
    public ExpiryStatus Classify(DateTime expiry, DateTime today)
    {
        int days = (expiry.Date - today.Date).Days;
        return days < 0 ? ExpiryStatus.Expired : days <= WarningDays ? ExpiryStatus.Soon : ExpiryStatus.Fresh;
    }
}

public sealed class PerishablePolicy : ExpiryPolicy { public override int WarningDays => 3; }
public sealed class ShelfStablePolicy : ExpiryPolicy { public override int WarningDays => 7; }

public static class FoodExtensions
{
    public static ExpiryStatus ExpiryStatus(this FoodItem item, DateTime today)
    {
        ExpiryPolicy policy = item.Category is FoodCategory.Produce or FoodCategory.Dairy or FoodCategory.Meat or FoodCategory.Bakery
            ? new PerishablePolicy() : new ShelfStablePolicy();
        return policy.Classify(item.ExpiresOn, today);
    }
}
