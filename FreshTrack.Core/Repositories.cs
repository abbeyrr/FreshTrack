using Microsoft.EntityFrameworkCore;

namespace FreshTrack.Core;

public interface IInventoryRepository
{
    Task<List<FoodItem>> GetAllAsync();
    Task<FoodItem?> GetAsync(int id);
    Task AddAsync(FoodItem item);
    Task SaveAsync();
    void Delete(FoodItem item);
}

public interface IShoppingRepository
{
    Task<List<ShoppingItem>> GetAllAsync();
    Task<ShoppingItem?> GetAsync(int id);
    Task AddAsync(ShoppingItem item);
    Task SaveAsync();
    void Delete(ShoppingItem item);
}

public sealed class EfInventoryRepository(FreshTrackDbContext db) : IInventoryRepository
{
    public Task<List<FoodItem>> GetAllAsync() => db.Foods.AsNoTracking().OrderBy(x => x.ExpiresOn).ToListAsync();
    public Task<FoodItem?> GetAsync(int id) => db.Foods.FindAsync(id).AsTask();
    public Task AddAsync(FoodItem item) => db.Foods.AddAsync(item).AsTask();
    public Task SaveAsync() => db.SaveChangesAsync();
    public void Delete(FoodItem item) => db.Foods.Remove(item);
}

public sealed class EfShoppingRepository(FreshTrackDbContext db) : IShoppingRepository
{
    public Task<List<ShoppingItem>> GetAllAsync() => db.ShoppingItems.AsNoTracking().OrderBy(x => x.IsPurchased).ThenBy(x => x.Name).ToListAsync();
    public Task<ShoppingItem?> GetAsync(int id) => db.ShoppingItems.FindAsync(id).AsTask();
    public Task AddAsync(ShoppingItem item) => db.ShoppingItems.AddAsync(item).AsTask();
    public Task SaveAsync() => db.SaveChangesAsync();
    public void Delete(ShoppingItem item) => db.ShoppingItems.Remove(item);
}
