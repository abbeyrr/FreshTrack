using FreshTrack.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FreshTrack.Tests;

[TestFixture]
public sealed class FreshTrackTests
{
    [Test]
    public void ExpiryBoundary_UsesCategoryPolicyAndTodayIsSoon()
    {
        DateTime today = new(2026, 10, 5);
        var dairy = new FoodItem { Category = FoodCategory.Dairy, ExpiresOn = today.AddDays(3) };
        var pantry = new FoodItem { Category = FoodCategory.Pantry, ExpiresOn = today.AddDays(7) };
        Assert.Multiple(() =>
        {
            Assert.That(dairy.ExpiryStatus(today), Is.EqualTo(ExpiryStatus.Soon));
            Assert.That(pantry.ExpiryStatus(today), Is.EqualTo(ExpiryStatus.Soon));
            Assert.That(new FoodItem { Category = FoodCategory.Dairy, ExpiresOn = today.AddDays(4) }.ExpiryStatus(today), Is.EqualTo(ExpiryStatus.Fresh));
            Assert.That(new FoodItem { Category = FoodCategory.Dairy, ExpiresOn = today.AddDays(-1) }.ExpiryStatus(today), Is.EqualTo(ExpiryStatus.Expired));
        });
    }

    [Test]
    public void ValidationRejectsBoundaryInputs()
    {
        var food = new FoodItem { Name = " ", Quantity = 0, PurchasedOn = DateTime.Today, ExpiresOn = DateTime.Today };
        Assert.Throws<ArgumentException>(() => InventoryService.Validate(food));
        food.Name = "Apples";
        Assert.Throws<ArgumentException>(() => InventoryService.Validate(food));
        food.Quantity = 1;
        food.ExpiresOn = food.PurchasedOn.AddDays(-1);
        Assert.Throws<ArgumentException>(() => InventoryService.Validate(food));
    }

    [Test]
    public async Task PurchaseAndPartialWaste_PersistAcrossContextsAndFeedReports()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite(connection).Options;
        DateTime today = DateTime.Today;
        int foodId;
        await using (var db = new FreshTrackDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var foodRepo = new EfInventoryRepository(db);
            var list = new ShoppingService(new EfShoppingRepository(db), foodRepo);
            await list.AddAsync("Apples", FoodCategory.Produce, 5);
            int shoppingId = (await list.GetAllAsync()).Single().Id;
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await list.TransferToInventoryAsync(shoppingId, today.AddDays(5), today));
            await list.MarkPurchasedAsync(shoppingId, today);
            Assert.That((await foodRepo.GetAllAsync()), Is.Empty);
            var pending = (await list.GetAllAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(pending.IsPurchased, Is.True);
                Assert.That(pending.AddedToInventory, Is.False);
            });
            await list.TransferToInventoryAsync(shoppingId, today.AddDays(5), today);
            Assert.That(await list.GetAllAsync(), Is.Empty);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await list.TransferToInventoryAsync(shoppingId, today.AddDays(5), today));
            Assert.That((await foodRepo.GetAllAsync()).Count, Is.EqualTo(1));
            foodId = (await foodRepo.GetAllAsync()).Single().Id;
            await new InventoryService(foodRepo).RecordAsync(foodId, 2, FoodState.Wasted, today);
        }
        await using (var reopened = new FreshTrackDbContext(options))
        {
            var foodRepo = new EfInventoryRepository(reopened);
            var foods = await foodRepo.GetAllAsync();
            Assert.That(foods.Single(x => x.State == FoodState.InStock).Quantity, Is.EqualTo(3));
            Assert.That(foods.Single(x => x.State == FoodState.Wasted).Quantity, Is.EqualTo(2));
            var historical = foods.Single(x => x.State == FoodState.Wasted);
            var closedError = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new InventoryService(foodRepo).RecordAsync(historical.Id, 1, FoodState.Used, today));
            Assert.That(closedError!.Message, Does.Contain("historical"));
            Assert.That(await new EfShoppingRepository(reopened).GetAllAsync(), Is.Empty);
            var report = await new ReportService(foodRepo).BuildAsync(today);
            Assert.That(report.Categories.Single().Wasted, Is.EqualTo(2));
            Assert.ThrowsAsync<ArgumentException>(async () => await new InventoryService(foodRepo).RecordAsync(foodId, 4, FoodState.Used, today));
        }
    }

    [Test]
    public async Task InventoryAddEditDelete_PersistsAndDoesNotReseed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite(connection).Options;
        int id;
        await using (var db = new FreshTrackDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var service = new InventoryService(new EfInventoryRepository(db));
            id = await service.SaveAsync(new FoodItem
            {
                Name = "Bananas", Category = FoodCategory.Produce, Quantity = 3,
                PurchasedOn = DateTime.Today, ExpiresOn = DateTime.Today.AddDays(4)
            });
            await service.SaveAsync(new FoodItem
            {
                Id = id, Name = "Bananas", Category = FoodCategory.Produce, Quantity = 6,
                PurchasedOn = DateTime.Today, ExpiresOn = DateTime.Today.AddDays(4)
            });
            Assert.That((await service.GetAllAsync()).Single().Quantity, Is.EqualTo(6));
            await service.DeleteAsync(id);
        }
        await using (var reopened = new FreshTrackDbContext(options))
        {
            await DemoData.InitializeAsync(reopened, DateTime.Today);
            Assert.That(await reopened.Foods.CountAsync(), Is.Zero);
        }
    }

    [Test]
    public async Task RepeatedUseAndExistingHistory_AreCombinedWithoutChangingTotals()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite(connection).Options;
        DateTime today = DateTime.Today;
        await using var db = new FreshTrackDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new InventoryService(new EfInventoryRepository(db));
        int id = await service.SaveAsync(new FoodItem
        {
            Name = "Milk", Category = FoodCategory.Dairy, Quantity = 4,
            PurchasedOn = today, ExpiresOn = today.AddDays(3)
        });

        await service.RecordAsync(id, 1, FoodState.Used, today);
        await service.RecordAsync(id, 1, FoodState.Used, today);
        var afterTwoUses = await service.GetAllAsync();
        Assert.Multiple(() =>
        {
            Assert.That(afterTwoUses.Count(x => x.State == FoodState.Used), Is.EqualTo(1));
            Assert.That(afterTwoUses.Single(x => x.State == FoodState.Used).Quantity, Is.EqualTo(2));
            Assert.That(afterTwoUses.Single(x => x.State == FoodState.InStock).Quantity, Is.EqualTo(2));
        });

        // Simulate a duplicate row saved by an older version of the application.
        db.Foods.Add(new FoodItem
        {
            Name = "Milk", Category = FoodCategory.Dairy, Quantity = 1,
            PurchasedOn = today, ExpiresOn = today.AddDays(3),
            State = FoodState.Used, ClosedOn = today
        });
        await db.SaveChangesAsync();
        await service.ConsolidateHistoryAsync();
        var consolidated = await service.GetAllAsync();
        Assert.That(consolidated.Count(x => x.State == FoodState.Used), Is.EqualTo(1));
        Assert.That(consolidated.Single(x => x.State == FoodState.Used).Quantity, Is.EqualTo(3));

        await service.RecordAsync(id, 2, FoodState.Used, today);
        var finalRows = await service.GetAllAsync();
        Assert.That(finalRows, Has.Count.EqualTo(1));
        Assert.That(finalRows.Single().Quantity, Is.EqualTo(5));
        Assert.That(finalRows.Single().State, Is.EqualTo(FoodState.Used));
        var report = await new ReportService(new EfInventoryRepository(db)).BuildAsync(today);
        Assert.That(report.Categories.Single().Used, Is.EqualTo(5));
    }

    [Test]
    public async Task OldTransferredRows_AreRemovedDuringSchemaUpgrade()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ShoppingItems (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL, Category INTEGER NOT NULL, Quantity INTEGER NOT NULL,
                    IsPurchased INTEGER NOT NULL, PurchasedOn TEXT NULL);
                INSERT INTO ShoppingItems (Name, Category, Quantity, IsPurchased, PurchasedOn)
                VALUES ('Old purchase', 0, 2, 1, '2026-10-06 00:00:00'),
                       ('Still to buy', 0, 1, 0, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite(connection).Options;
        await using var db = new FreshTrackDbContext(options);
        await DemoData.InitializeAsync(db, DateTime.Today);
        var rows = await db.ShoppingItems.ToListAsync();
        Assert.That(rows.Any(x => x.Name == "Old purchase"), Is.False);
        Assert.That(rows.Single(x => x.Name == "Still to buy").AddedToInventory, Is.False);
    }

    [Test]
    public async Task PriorTwoStepTransferredRows_AreRemovedButPendingPurchasesRemain()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite(connection).Options;
        await using var db = new FreshTrackDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.ShoppingItems.AddRange(
            new ShoppingItem { Name = "Transferred", Category = FoodCategory.Dairy, Quantity = 1, IsPurchased = true, AddedToInventory = true },
            new ShoppingItem { Name = "Pending", Category = FoodCategory.Dairy, Quantity = 2, IsPurchased = true, AddedToInventory = false });
        await db.SaveChangesAsync();

        await DemoData.InitializeAsync(db, DateTime.Today);
        var remaining = await db.ShoppingItems.AsNoTracking().ToListAsync();
        Assert.That(remaining.Select(x => x.Name), Is.EqualTo(new[] { "Pending" }));
    }

    [Test]
    public void SimulatedModel_TrainsOnSeparateDataAndGroupsCurrentStock()
    {
        var first = WasteRiskDemoService.Generate(20, WasteRiskDemoService.TrainingSeed);
        var repeated = WasteRiskDemoService.Generate(20, WasteRiskDemoService.TrainingSeed);
        Assert.That(first.Select(x => (x.Category, x.ShelfLifeDays, x.Summer, x.DaysUntilExpiry,
                x.Quantity, x.Profile, x.AgeDays, x.Label)),
            Is.EqualTo(repeated.Select(x => (x.Category, x.ShelfLifeDays, x.Summer, x.DaysUntilExpiry,
                x.Quantity, x.Profile, x.AgeDays, x.Label))));

        string folder = Path.Combine(Path.GetTempPath(), $"FreshTrack-ml-test-{Guid.NewGuid():N}");
        try
        {
            DateTime today = new(2026, 10, 7);
            var stock = new List<FoodItem>
            {
                new() { Name = "test_expire", Category = FoodCategory.Meat, Quantity = 1,
                    PurchasedOn = today, ExpiresOn = today.AddDays(1) },
                new() { Name = "Meat one day left, ten-day shelf life", Category = FoodCategory.Meat,
                    Quantity = 1, PurchasedOn = today.AddDays(-9), ExpiresOn = today.AddDays(1) },
                new() { Name = "Meat with ten days left", Category = FoodCategory.Meat, Quantity = 1,
                    PurchasedOn = today, ExpiresOn = today.AddDays(10) },
                new() { Name = "Apples", Category = FoodCategory.Produce, Quantity = 3,
                    PurchasedOn = today, ExpiresOn = today.AddDays(6) },
                new() { Name = "Milk", Category = FoodCategory.Dairy, Quantity = 2,
                    PurchasedOn = today, ExpiresOn = today.AddDays(5) },
                new() { Name = "Old bread", Category = FoodCategory.Bakery, Quantity = 1,
                    PurchasedOn = today.AddDays(-3), ExpiresOn = today, State = FoodState.Wasted }
            };
            var service = new WasteRiskDemoService();
            var report = service.TrainAndPredict(stock, folder, today);
            TestContext.WriteLine($"Synthetic held-out metrics: AUC={report.Auc:F3}, F1={report.F1:F3}, " +
                $"precision={report.Precision:F3}, recall={report.Recall:F3}, threshold={report.Threshold:P0}");
            var urgent = report.Items.Single(x => x.Name == "test_expire");
            var sameShelfUrgent = report.Items.Single(x => x.Name == "Meat one day left, ten-day shelf life");
            var freshMeat = report.Items.Single(x => x.Name == "Meat with ten days left");
            var newlyAdded = stock.Append(new FoodItem { Name = "New item", Category = FoodCategory.Frozen,
                Quantity = 1, PurchasedOn = today, ExpiresOn = today.AddDays(30) }).ToList();
            var rescored = service.ScoreSavedModel(newlyAdded, folder, today);
            var syntheticRows = WasteRiskDemoService.Generate(WasteRiskDemoService.TrainingCount,
                WasteRiskDemoService.TrainingSeed);
            int oneDayMeatCount = syntheticRows.Count(x => x.Category == "Meat" && x.ShelfLifeDays == 1);
            TestContext.WriteLine($"Synthetic training coverage: one-day meat=" +
                $"{oneDayMeatCount}, " +
                $"unused-label rate={syntheticRows.Average(x => x.Label ? 1d : 0d):P1}");
            Assert.Multiple(() =>
            {
                Assert.That(report.TrainingRows, Is.EqualTo(40_000));
                Assert.That(report.TestRows, Is.EqualTo(10_000));
                Assert.That(report.Categories.Sum(x => x.StockItems), Is.EqualTo(5));
                Assert.That(report.Categories, Has.Count.EqualTo(3));
                Assert.That(report.Categories.All(x => x.AverageProbability is >= 0 and <= 1), Is.True);
                Assert.That(report.Threshold, Is.InRange(0.50f, 0.85f));
                Assert.That(rescored.Threshold, Is.EqualTo(report.Threshold));
                Assert.That(urgent.DaysLeft, Is.EqualTo(1));
                Assert.That(urgent.Status, Is.EqualTo(ExpiryStatus.Soon));
                Assert.That(urgent.AttentionStatus(report.Threshold),
                    Is.EqualTo("High risk"));
                Assert.That(freshMeat.AttentionStatus(report.Threshold),
                    Is.EqualTo(freshMeat.Probability >= report.Threshold ? "High risk" : "Lower risk"));
                Assert.That(new FoodRiskRow(99, "Expired", FoodCategory.Meat, 1, today.AddDays(-1),
                    -1, ExpiryStatus.Expired, 0.1).AttentionStatus(report.Threshold), Is.EqualTo("Expired"));
                Assert.That(new FoodRiskRow(100, "Far but high risk", FoodCategory.Meat, 4,
                    today.AddDays(20), 20, ExpiryStatus.Fresh, 0.9)
                    .AttentionStatus(report.Threshold), Is.EqualTo("High risk"));
                Assert.That(new FoodRiskRow(101, "Near but lower risk", FoodCategory.Meat, 1,
                    today.AddDays(1), 1, ExpiryStatus.Soon, 0.1)
                    .AttentionStatus(report.Threshold), Is.EqualTo("Lower risk"));
                Assert.That(urgent.Probability, Is.GreaterThan(freshMeat.Probability));
                Assert.That(urgent.Probability, Is.GreaterThan(0.6));
                Assert.That(sameShelfUrgent.Probability, Is.GreaterThan(freshMeat.Probability));
                Assert.That(rescored.Items, Has.Count.EqualTo(6));
                Assert.That(rescored.Items.Any(x => x.Name == "New item"), Is.True);
                Assert.That(rescored.Categories.Single(x => x.Category == FoodCategory.Meat).StockItems, Is.EqualTo(3));
                Assert.That(report.Categories.Sum(x => x.HighRiskItems),
                    Is.EqualTo(report.Items.Count(x => x.Probability >= report.Threshold)));
                Assert.That(report.Auc, Is.GreaterThan(0.6));
                Assert.That(syntheticRows.Any(x => x.Category == "Meat" && x.ShelfLifeDays == 1), Is.True);
                Assert.That(syntheticRows.All(x => x.AgeDays >= 0
                    && x.ShelfLifeDays - x.AgeDays == x.DaysUntilExpiry
                    && x.Quantity is >= 1 and <= 8 && !string.IsNullOrWhiteSpace(x.Profile)), Is.True);
                Assert.That(syntheticRows.Any(x => x.Label), Is.True);
                Assert.That(syntheticRows.Any(x => !x.Label), Is.True);
                Assert.That(File.ReadLines(Path.Combine(folder, "synthetic_training.csv")).Count(), Is.EqualTo(40_001));
                Assert.That(File.ReadLines(Path.Combine(folder, "synthetic_test.csv")).Count(), Is.EqualTo(10_001));
                Assert.That(File.Exists(Path.Combine(folder, "waste_demo_model_v2.zip")), Is.True);
                Assert.That(File.Exists(Path.Combine(folder, "waste_demo_threshold_v2.txt")), Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
