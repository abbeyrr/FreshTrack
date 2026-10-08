namespace FreshTrack.Core;

public sealed class ShoppingService(IShoppingRepository shopping, IInventoryRepository inventory)
{
    public Task<List<ShoppingItem>> GetAllAsync() => shopping.GetAllAsync();

    public async Task AddAsync(string name, FoodCategory category, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new ArgumentException("Shopping item name must contain 1–120 characters.");
        if (quantity <= 0) throw new ArgumentException("Quantity must be greater than zero.");
        await shopping.AddAsync(new ShoppingItem { Name = name.Trim(), Category = category, Quantity = quantity });
        await shopping.SaveAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var item = await shopping.GetAsync(id) ?? throw new InvalidOperationException("Shopping item no longer exists.");
        shopping.Delete(item);
        await shopping.SaveAsync();
    }

    public async Task MarkPurchasedAsync(int id, DateTime today)
    {
        var item = await shopping.GetAsync(id) ?? throw new InvalidOperationException("Shopping item no longer exists.");
        if (item.IsPurchased) throw new InvalidOperationException("This item is already marked as purchased.");
        item.IsPurchased = true;
        item.PurchasedOn = today.Date;
        await shopping.SaveAsync();
    }

    public async Task TransferToInventoryAsync(int id, DateTime expiry, DateTime today)
    {
        var item = await shopping.GetAsync(id) ?? throw new InvalidOperationException("Shopping item no longer exists.");
        if (!item.IsPurchased) throw new InvalidOperationException("Mark this item as purchased before adding it to inventory.");
        if (item.AddedToInventory) throw new InvalidOperationException("This item is already in inventory.");
        DateTime purchasedOn = item.PurchasedOn?.Date ?? today.Date;
        if (expiry.Date < purchasedOn) throw new ArgumentException("Expiry date cannot precede purchase date.");
        var food = new FoodItem
        {
            Name = item.Name, Category = item.Category, Quantity = item.Quantity,
            PurchasedOn = purchasedOn, ExpiresOn = expiry.Date
        };
        InventoryService.Validate(food);
        await inventory.AddAsync(food);
        shopping.Delete(item);
        // Both repositories share one DbContext. One SaveChanges adds the food
        // and removes its shopping row in the same database transaction.
        await shopping.SaveAsync();
    }
}
