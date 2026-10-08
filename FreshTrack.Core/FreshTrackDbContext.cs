using Microsoft.EntityFrameworkCore;

namespace FreshTrack.Core;

public sealed class FreshTrackDbContext(DbContextOptions<FreshTrackDbContext> options) : DbContext(options)
{
    public DbSet<FoodItem> Foods => Set<FoodItem>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FoodItem>().Property(x => x.Name).HasMaxLength(120).IsRequired();
        modelBuilder.Entity<ShoppingItem>().Property(x => x.Name).HasMaxLength(120).IsRequired();
        modelBuilder.Entity<FoodItem>().HasIndex(x => new { x.Name, x.State });
        modelBuilder.Entity<ShoppingItem>().HasIndex(x => x.IsPurchased);
    }
}
