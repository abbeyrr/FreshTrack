using Microsoft.EntityFrameworkCore;

namespace FreshTrack.Core;

public static class DemoData
{
    public static async Task InitializeAsync(FreshTrackDbContext db, DateTime today)
    {
        // Seed only when the schema is first created, never after a user deletes data.
        if (!await db.Database.EnsureCreatedAsync())
        {
            await UpgradeShoppingTransferStateAsync(db);
            return;
        }
        db.Foods.AddRange(
            new FoodItem { Name = "Milk", Category = FoodCategory.Dairy, Quantity = 2, PurchasedOn = today.AddDays(-2), ExpiresOn = today.AddDays(2) },
            new FoodItem { Name = "Spinach", Category = FoodCategory.Produce, Quantity = 1, PurchasedOn = today.AddDays(-5), ExpiresOn = today.AddDays(-1) },
            new FoodItem { Name = "Rice", Category = FoodCategory.Pantry, Quantity = 3, PurchasedOn = today.AddDays(-8), ExpiresOn = today.AddMonths(4) },
            new FoodItem { Name = "Bread", Category = FoodCategory.Bakery, Quantity = 2, PurchasedOn = today.AddDays(-5), ExpiresOn = today.AddDays(-2), State = FoodState.Wasted, ClosedOn = today.AddDays(-1) },
            new FoodItem { Name = "Eggs", Category = FoodCategory.Dairy, Quantity = 4, PurchasedOn = today.AddDays(-6), ExpiresOn = today.AddDays(3), State = FoodState.Used, ClosedOn = today.AddDays(-1) });
        db.ShoppingItems.AddRange(
            new ShoppingItem { Name = "Tomatoes", Category = FoodCategory.Produce, Quantity = 4 },
            new ShoppingItem { Name = "Yoghurt", Category = FoodCategory.Dairy, Quantity = 2 });
        await db.SaveChangesAsync();
    }

    private static async Task UpgradeShoppingTransferStateAsync(FreshTrackDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('ShoppingItems')";
            await using var reader = await command.ExecuteReaderAsync();
            bool hasColumn = false;
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "AddedToInventory", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
            await reader.CloseAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (!hasColumn)
            {
                // The original one-step flow already created food for every
                // purchased row, so remove those rows rather than transferring twice.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShoppingItems ADD COLUMN AddedToInventory INTEGER NOT NULL DEFAULT 0");
                await db.Database.ExecuteSqlRawAsync("UPDATE ShoppingItems SET AddedToInventory = 1 WHERE IsPurchased = 1");
            }
            // The previous two-step version retained transferred rows; only
            // those marked transferred are removed. Pending purchases remain.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ShoppingItems WHERE AddedToInventory = 1");
            await transaction.CommitAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
