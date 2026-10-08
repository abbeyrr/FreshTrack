namespace FreshTrack.Core;

public sealed class InventoryService(IInventoryRepository repository)
{
    public Task<List<FoodItem>> GetAllAsync() => repository.GetAllAsync();

    public static void Validate(FoodItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 120)
            throw new ArgumentException("Food name must contain 1–120 characters.");
        if (item.Quantity <= 0) throw new ArgumentException("Quantity must be greater than zero.");
        if (item.PurchasedOn.Date > DateTime.Today) throw new ArgumentException("Purchase date cannot be in the future.");
        if (item.ExpiresOn.Date < item.PurchasedOn.Date) throw new ArgumentException("Expiry date cannot precede purchase date.");
    }

    public async Task<int> SaveAsync(FoodItem input)
    {
        Validate(input);
        FoodItem item = input.Id == 0 ? new FoodItem() : await repository.GetAsync(input.Id)
            ?? throw new InvalidOperationException("Food item no longer exists.");
        item.Name = input.Name.Trim();
        item.Category = input.Category;
        item.Quantity = input.Quantity;
        item.PurchasedOn = input.PurchasedOn.Date;
        item.ExpiresOn = input.ExpiresOn.Date;
        item.Notes = input.Notes.Trim();
        if (input.Id == 0) await repository.AddAsync(item);
        await repository.SaveAsync();
        return item.Id;
    }

    public async Task DeleteAsync(int id)
    {
        var item = await repository.GetAsync(id) ?? throw new InvalidOperationException("Food item no longer exists.");
        repository.Delete(item);
        await repository.SaveAsync();
    }

    // Older versions saved every use as a separate row. Merge only records with
    // the same food details, outcome, and day so monthly report dates stay intact.
    public async Task ConsolidateHistoryAsync()
    {
        var groups = (await repository.GetAllAsync())
            .Where(x => x.State != FoodState.InStock && x.ClosedOn.HasValue)
            .GroupBy(x => new { x.Name, x.Category, x.PurchasedOn, x.ExpiresOn, x.Notes, x.State, Day = x.ClosedOn!.Value.Date })
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var rows = group.OrderBy(x => x.Id).ToList();
            var keeper = await repository.GetAsync(rows[0].Id) ?? throw new InvalidOperationException("Food item no longer exists.");
            keeper.Quantity = rows.Sum(x => x.Quantity);
            foreach (var duplicate in rows.Skip(1))
            {
                var row = await repository.GetAsync(duplicate.Id) ?? throw new InvalidOperationException("Food item no longer exists.");
                repository.Delete(row);
            }
        }

        if (groups.Count > 0) await repository.SaveAsync();
    }

    public async Task RecordAsync(int id, int amount, FoodState result, DateTime today)
    {
        if (result == FoodState.InStock) throw new ArgumentException("Choose Used or Wasted.");
        var item = await repository.GetAsync(id) ?? throw new InvalidOperationException("Food item no longer exists.");
        if (item.State != FoodState.InStock)
            throw new InvalidOperationException($"{item.Name} is already recorded as {item.State}; its quantity is historical, not available stock.");
        if (amount <= 0 || amount > item.Quantity)
            throw new ArgumentException("Amount must be between 1 and the available quantity.");

        var matching = (await repository.GetAllAsync())
            .Where(x => x.State == result && x.ClosedOn?.Date == today.Date
                && x.Name == item.Name && x.Category == item.Category
                && x.PurchasedOn == item.PurchasedOn && x.ExpiresOn == item.ExpiresOn
                && x.Notes == item.Notes)
            .OrderBy(x => x.Id).ToList();
        FoodItem? history = null;
        if (matching.Count > 0)
        {
            history = await repository.GetAsync(matching[0].Id) ?? throw new InvalidOperationException("Food item no longer exists.");
            history.Quantity += amount + matching.Skip(1).Sum(x => x.Quantity);
            foreach (var duplicate in matching.Skip(1))
            {
                var row = await repository.GetAsync(duplicate.Id) ?? throw new InvalidOperationException("Food item no longer exists.");
                repository.Delete(row);
            }
        }
        if (amount == item.Quantity)
        {
            if (history is null)
            {
                item.State = result;
                item.ClosedOn = today.Date;
            }
            else repository.Delete(item);
        }
        else
        {
            item.Quantity -= amount;
            if (history is null) await repository.AddAsync(new FoodItem
            {
                Name = item.Name, Category = item.Category, Quantity = amount,
                PurchasedOn = item.PurchasedOn, ExpiresOn = item.ExpiresOn,
                State = result, ClosedOn = today.Date, Notes = item.Notes
            });
        }
        await repository.SaveAsync();
    }
}
