namespace FreshTrack.Core;

public sealed class ReportService(IInventoryRepository inventory)
{
    public async Task<(List<CategoryReport> Categories, List<MonthlyReport> Months, int Soon, int Expired)> BuildAsync(DateTime today)
    {
        List<FoodItem> items = await inventory.GetAllAsync();
        var categories = items.GroupBy(x => x.Category)
            .Select(g => new CategoryReport(g.Key,
                g.Where(x => x.State == FoodState.InStock && x.ExpiryStatus(today) != ExpiryStatus.Expired).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.Used).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.Wasted).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.InStock && x.ExpiryStatus(today) == ExpiryStatus.Expired).Sum(x => x.Quantity)))
            .OrderBy(x => x.Category).ToList();

        // Explicit anonymous delegate combined with a LINQ lambda: select recent
        // inventory activity before grouping it into the twelve-month report.
        Func<FoodItem, bool> inLastYear = delegate(FoodItem x)
        {
            return x.PurchasedOn.Date >= today.Date.AddMonths(-12) ||
                   (x.ClosedOn.HasValue && x.ClosedOn.Value.Date >= today.Date.AddMonths(-12));
        };
        var months = items.Where(x => inLastYear(x))
            .GroupBy(x => (x.ClosedOn ?? x.PurchasedOn).ToString("yyyy-MM"))
            .Select(g => new MonthlyReport(g.Key,
                g.Where(x => x.State == FoodState.InStock && x.ExpiryStatus(today) != ExpiryStatus.Expired).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.Used).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.Wasted).Sum(x => x.Quantity),
                g.Where(x => x.State == FoodState.InStock && x.ExpiryStatus(today) == ExpiryStatus.Expired).Sum(x => x.Quantity)))
            .OrderBy(x => x.Month).ToList();
        int soon = items.Count(x => x.State == FoodState.InStock && x.ExpiryStatus(today) == ExpiryStatus.Soon);
        int expired = items.Count(x => x.State == FoodState.InStock && x.ExpiryStatus(today) == ExpiryStatus.Expired);
        return (categories, months, soon, expired);
    }
}
