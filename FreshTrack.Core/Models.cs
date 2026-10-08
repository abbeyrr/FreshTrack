namespace FreshTrack.Core;

public enum FoodCategory { Produce, Dairy, Meat, Bakery, Pantry, Frozen, Other }
public enum FoodState { InStock, Used, Wasted }
public enum ExpiryStatus { Expired, Soon, Fresh }

public sealed class FoodItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public FoodCategory Category { get; set; }
    public int Quantity { get; set; }
    public DateTime PurchasedOn { get; set; }
    public DateTime ExpiresOn { get; set; }
    public FoodState State { get; set; } = FoodState.InStock;
    public DateTime? ClosedOn { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class ShoppingItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public FoodCategory Category { get; set; }
    public int Quantity { get; set; }
    public bool IsPurchased { get; set; }
    public DateTime? PurchasedOn { get; set; }
    public bool AddedToInventory { get; set; }
}

public sealed record CategoryReport(FoodCategory Category, int InStock, int Used, int Wasted);
public sealed record MonthlyReport(string Month, int InStock, int Used, int Wasted);
