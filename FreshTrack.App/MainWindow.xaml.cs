using FreshTrack.Core;
using Microsoft.EntityFrameworkCore;
using System.Windows;
using System.Windows.Controls;

namespace FreshTrack.App;

public partial class MainWindow : Window
{
    private readonly FreshTrackDbContext db;
    private readonly InventoryService inventory;
    private readonly ShoppingService shopping;
    private readonly ReportService reports;
    private readonly WasteRiskDemoService wasteDemo = new();
    private readonly SemaphoreSlim demoModelGate = new(1, 1);
    private List<FoodItem> cachedFoods = [];
    private RiskScores? currentRiskScores;
    private bool demoModelReady;
    private bool demoTrainingInProgress;
    private string lastScoredStockSignature = string.Empty;
    private int editingFoodId;

    public MainWindow()
    {
        InitializeComponent();
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreshTrack");
        System.IO.Directory.CreateDirectory(folder);
        string file = System.IO.Path.Combine(folder, "freshtrack.db");
        var options = new DbContextOptionsBuilder<FreshTrackDbContext>().UseSqlite($"Data Source={file}").Options;
        db = new FreshTrackDbContext(options);
        var foodRepo = new EfInventoryRepository(db);
        inventory = new InventoryService(foodRepo);
        shopping = new ShoppingService(new EfShoppingRepository(db), foodRepo);
        reports = new ReportService(foodRepo);
        FoodCategoryBox.ItemsSource = Enum.GetValues<FoodCategory>();
        ShoppingCategoryBox.ItemsSource = Enum.GetValues<FoodCategory>();
        FoodCategoryBox.SelectedIndex = 0;
        ShoppingCategoryBox.SelectedIndex = 0;
        ExpiryFilter.ItemsSource = new[] { "All", "Expired", "High risk", "Low risk",
            "Date: Soon", "Date: Fresh" };
        ExpiryFilter.SelectedIndex = 0;
        PurchaseDate.SelectedDate = DateTime.Today;
        ExpiryDate.SelectedDate = DateTime.Today.AddDays(3);
        ShoppingExpiryDate.SelectedDate = DateTime.Today.AddDays(3);
        UpdateRecordAvailability(null);
        UpdateShoppingTransferAvailability(null);
        Closed += (_, _) => db.Dispose();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await DemoData.InitializeAsync(db, DateTime.Today);
        await inventory.ConsolidateHistoryAsync();
        await RefreshAllAsync();
    }, "Database ready. Demo data is added only when the database is first created.");

    private async Task<bool> RunAsync(Func<Task> action, string success)
    {
        try { await action(); FeedbackText.Text = success; return true; }
        catch (Exception ex)
        {
            FeedbackText.Text = $"Error: {ex.Message}";
            MessageBox.Show(this, ex.Message, "FreshTrack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task RefreshAllAsync()
    {
        cachedFoods = await inventory.GetAllAsync();
        int availableUnits = cachedFoods.Where(x => x.State == FoodState.InStock).Sum(x => x.Quantity);
        int expiredUnits = cachedFoods.Where(x => x.State == FoodState.InStock &&
            x.ExpiryStatus(DateTime.Today) == ExpiryStatus.Expired).Sum(x => x.Quantity);
        InventorySummary.Text = $"{cachedFoods.Count} records  •  {availableUnits} units in inventory, including {expiredUnits} expired";
        ShowInventory();
        var shoppingItems = await shopping.GetAllAsync();
        ShoppingGrid.ItemsSource = shoppingItems;
        ShoppingSummary.Text = $"{shoppingItems.Count(x => !x.IsPurchased)} to buy  •  {shoppingItems.Count(x => x.IsPurchased)} purchased, awaiting inventory";
        UpdateShoppingTransferAvailability(ShoppingGrid.SelectedItem as ShoppingItem);
        var result = await reports.BuildAsync(DateTime.Today);
        CategoryGrid.ItemsSource = result.Categories;
        MonthlyGrid.ItemsSource = result.Months;
        int stock = result.Categories.Sum(x => x.InStock);
        int reportExpiredUnits = result.Categories.Sum(x => x.ExpiredUnits);
        int used = result.Categories.Sum(x => x.Used);
        int wasted = result.Categories.Sum(x => x.Wasted);
        StockCount.Text = stock.ToString();
        UsedCount.Text = used.ToString();
        WastedCount.Text = wasted.ToString();
        ReportSummary.Text = $"Available: {stock} units  •  Expired: {result.Expired} items / {reportExpiredUnits} units  •  Used: {used} units  •  Wasted: {wasted} units  •  Expiring: {result.Soon} items";
        int maxWaste = Math.Max(1, result.Categories.Count == 0 ? 0 : result.Categories.Max(x => x.Wasted));
        WasteBars.ItemsSource = result.Categories.Select(x => new BarRow(x.Category.ToString(), 100d * x.Wasted / maxWaste)).ToList();
        await RefreshDemoScoresIfNeededAsync();
        ShowExpiry();
    }

    private static string DemoDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreshTrack", "ml-demo");

    private static string StockSignature(IEnumerable<FoodItem> foods, DateTime today) =>
        today.Date.Ticks + "|" + string.Join("|", foods.Where(x => x.State == FoodState.InStock)
            .OrderBy(x => x.Id).Select(x =>
                $"{x.Id}:{x.Name}:{x.Category}:{x.Quantity}:{x.PurchasedOn.Ticks}:{x.ExpiresOn.Ticks}"));

    private async Task RefreshDemoScoresIfNeededAsync()
    {
        await demoModelGate.WaitAsync();
        try
        {
            if (demoTrainingInProgress) return;
            DateTime today = DateTime.Today;
            string signature = StockSignature(cachedFoods, today);
            if (demoModelReady && signature == lastScoredStockSignature) return;
            var snapshot = cachedFoods.ToList();
            RiskScores scores;
            if (!demoModelReady && !wasteDemo.HasSavedModel(DemoDirectory))
            {
                DemoRiskReport trained = await Task.Run(() => wasteDemo.TrainAndPredict(snapshot, DemoDirectory, today));
                scores = new RiskScores(trained.Items, trained.Categories, trained.Threshold);
            }
            else
            {
                scores = await Task.Run(() => wasteDemo.ScoreSavedModel(snapshot, DemoDirectory, today));
            }
            BindRiskScores(scores);
            lastScoredStockSignature = signature;
            demoModelReady = true;
        }
        finally
        {
            demoModelGate.Release();
        }
    }

    private void BindRiskScores(RiskScores scores)
    {
        currentRiskScores = scores;
    }

    private void ShowInventory()
    {
        string query = SearchBox.Text.Trim();
        FoodGrid.ItemsSource = cachedFoods.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.Category.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ExpiresOn).ToList();
    }

    private void ShowExpiry()
    {
        DateTime today = DateTime.Today;
        var riskById = currentRiskScores?.Items.ToDictionary(x => x.FoodId) ?? [];
        var rows = cachedFoods.Where(x => x.State == FoodState.InStock).Select(x =>
        {
            ExpiryStatus dateStatus = x.ExpiryStatus(today);
            riskById.TryGetValue(x.Id, out FoodRiskRow? risk);
            string priority = dateStatus == ExpiryStatus.Expired ? "Expired" : risk is null ? "Not rated" :
                risk.AttentionStatus(currentRiskScores!.Threshold);
            if (priority == "Lower risk") priority = "Low risk";
            return new ExpiryRow(x.Name, x.Category, x.Quantity, x.ExpiresOn,
                priority, dateStatus, (x.ExpiresOn.Date - today).Days,
                dateStatus == ExpiryStatus.Expired ? "—" : risk?.SimulatedRisk ?? "—");
        }).ToList();
        string filter = ExpiryFilter.SelectedItem?.ToString() ?? "All";
        ExpiryGrid.ItemsSource = rows.Where(x => filter == "All" || x.Status == filter ||
                filter == "Date: Soon" && x.DateStatus == ExpiryStatus.Soon ||
                filter == "Date: Fresh" && x.DateStatus == ExpiryStatus.Fresh)
            .OrderBy(x => x.Status == "Expired" ? 0 : x.Status == "High risk" ? 1 : 2)
            .ThenBy(x => x.DaysLeft).ToList();
        int expired = rows.Count(x => x.Status == "Expired");
        int highRisk = rows.Count(x => x.Status == "High risk");
        int lowerRisk = rows.Count(x => x.Status == "Low risk");
        ExpiredCount.Text = expired.ToString();
        HighRiskCount.Text = highRisk.ToString();
        LowerRiskCount.Text = lowerRisk.ToString();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FoodGrid is not null) ShowInventory();
    }
    private void ExpiryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExpiryGrid is not null) ShowExpiry();
    }
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == MainTabs && IsLoaded) _ = RunAsync(RefreshAllAsync, "Views refreshed.");
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAllAsync, "Views refreshed.");

    private void FoodGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = FoodGrid.SelectedItem as FoodItem;
        UpdateRecordAvailability(item);
        if (item is null) return;
        editingFoodId = item.Id;
        FoodName.Text = item.Name;
        FoodCategoryBox.SelectedItem = item.Category;
        FoodQuantity.Text = item.Quantity.ToString();
        PurchaseDate.SelectedDate = item.PurchasedOn;
        ExpiryDate.SelectedDate = item.ExpiresOn;
        FoodNotes.Text = item.Notes;
    }

    private void UpdateRecordAvailability(FoodItem? item)
    {
        bool available = item?.State == FoodState.InStock;
        RecordQuantity.IsEnabled = available;
        UsedButton.IsEnabled = available;
        WastedButton.IsEnabled = available;
        RecordHelp.Text = item is null
            ? "Choose an in-stock row, then record the quantity used or wasted."
            : available
                ? $"{item.Name}: {item.Quantity} units available to record."
                : $"{item.Name} is already recorded as {item.State}. The quantity shown is historical, not available stock.";
    }

    private void ClearFood_Click(object sender, RoutedEventArgs e) => ClearFoodForm();
    private void ClearFoodForm()
    {
        FoodGrid.SelectedItem = null;
        UpdateRecordAvailability(null);
        editingFoodId = 0;
        FoodName.Clear(); FoodQuantity.Clear(); FoodNotes.Clear();
        FoodCategoryBox.SelectedIndex = 0;
        PurchaseDate.SelectedDate = DateTime.Today;
        ExpiryDate.SelectedDate = DateTime.Today.AddDays(3);
    }

    private async void SaveFood_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!int.TryParse(FoodQuantity.Text, out int quantity)) throw new ArgumentException("Quantity must be a whole number.");
        if (PurchaseDate.SelectedDate is null || ExpiryDate.SelectedDate is null) throw new ArgumentException("Choose purchase and expiry dates.");
        await inventory.SaveAsync(new FoodItem
        {
            Id = editingFoodId, Name = FoodName.Text, Category = (FoodCategory)FoodCategoryBox.SelectedItem,
            Quantity = quantity, PurchasedOn = PurchaseDate.SelectedDate.Value,
            ExpiresOn = ExpiryDate.SelectedDate.Value, Notes = FoodNotes.Text
        });
        ClearFoodForm(); await RefreshAllAsync();
    }, "Food saved.");

    private async void DeleteFood_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (FoodGrid.SelectedItem is not FoodItem item) throw new ArgumentException("Select a food item first.");
        if (MessageBox.Show(this, $"Delete {item.Name}?", "Confirm deletion", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await inventory.DeleteAsync(item.Id);
        ClearFoodForm(); await RefreshAllAsync();
    }, "Delete action completed.");

    private async Task RecordSelectedAsync(FoodState state) => await RunAsync(async () =>
    {
        if (FoodGrid.SelectedItem is not FoodItem item) throw new ArgumentException("Select a food item first.");
        if (!int.TryParse(RecordQuantity.Text, out int amount)) throw new ArgumentException("Record quantity must be a whole number.");
        await inventory.RecordAsync(item.Id, amount, state, DateTime.Today);
        ClearFoodForm(); await RefreshAllAsync();
    }, $"Quantity recorded as {state}.");
    private async void Used_Click(object sender, RoutedEventArgs e) => await RecordSelectedAsync(FoodState.Used);
    private async void Wasted_Click(object sender, RoutedEventArgs e) => await RecordSelectedAsync(FoodState.Wasted);

    private async void AddShopping_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!int.TryParse(ShoppingQuantity.Text, out int quantity)) throw new ArgumentException("Quantity must be a whole number.");
        await shopping.AddAsync(ShoppingName.Text, (FoodCategory)ShoppingCategoryBox.SelectedItem, quantity);
        ShoppingName.Clear(); ShoppingQuantity.Text = "1"; await RefreshAllAsync();
    }, "Shopping item added.");

    private async Task PurchaseItemAsync(ShoppingItem item)
    {
        if (ShoppingExpiryDate.SelectedDate is null) throw new ArgumentException("Choose an expiry date.");
        await shopping.TransferToInventoryAsync(item.Id, ShoppingExpiryDate.SelectedDate.Value, DateTime.Today);
        await RefreshAllAsync();
    }

    private void ShoppingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransferShoppingButton is not null)
            UpdateShoppingTransferAvailability(ShoppingGrid.SelectedItem as ShoppingItem);
    }

    private void UpdateShoppingTransferAvailability(ShoppingItem? item)
    {
        TransferShoppingButton.IsEnabled = item?.IsPurchased == true && !item.AddedToInventory;
        ShoppingStepText.Text = item is null
            ? "Tick Purchased, select the row, then choose an expiry date."
            : !item.IsPurchased
                ? $"Tick Purchased for {item.Name} first."
                : $"{item.Name} is purchased. Choose its expiry date, then add it to inventory. It will leave this list.";
    }

    private async void PurchaseShopping_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ShoppingGrid.SelectedItem is not ShoppingItem item) throw new ArgumentException("Select a shopping item first.");
        await PurchaseItemAsync(item);
    }, "Purchased item added to inventory and removed from shopping list.");

    private async void PurchasedCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not ShoppingItem item) return;
        ShoppingGrid.SelectedItem = item;
        box.IsEnabled = false;
        bool purchased = await RunAsync(async () =>
        {
            await shopping.MarkPurchasedAsync(item.Id, DateTime.Today);
            await RefreshAllAsync();
            ShoppingGrid.SelectedItem = (ShoppingGrid.ItemsSource as IEnumerable<ShoppingItem>)?
                .FirstOrDefault(x => x.Id == item.Id);
        }, "Item marked as purchased. Choose its expiry date and add it to inventory.");
        if (!purchased)
        {
            box.IsChecked = item.IsPurchased;
            box.IsEnabled = true;
        }
    }

    private async void DeleteShopping_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ShoppingGrid.SelectedItem is not ShoppingItem item) throw new ArgumentException("Select a shopping item first.");
        await shopping.DeleteAsync(item.Id); await RefreshAllAsync();
    }, "Shopping item deleted.");

    private async void TrainDemoModel_Click(object sender, RoutedEventArgs e)
    {
        TrainDemoButton.IsEnabled = false;
        await demoModelGate.WaitAsync();
        demoTrainingInProgress = true;
        FeedbackText.Text = "Updating risk estimates…";
        var stockSnapshot = cachedFoods.ToList();
        DateTime today = DateTime.Today;
        bool success = false;
        try
        {
            success = await RunAsync(async () =>
            {
                DemoRiskReport result = await Task.Run(() => wasteDemo.TrainAndPredict(stockSnapshot, DemoDirectory, today));
                BindRiskScores(new RiskScores(result.Items, result.Categories, result.Threshold));
                lastScoredStockSignature = StockSignature(stockSnapshot, today);
                demoModelReady = true;
            }, "Risk estimates updated.");
        }
        finally
        {
            demoTrainingInProgress = false;
            demoModelGate.Release();
            TrainDemoButton.IsEnabled = true;
        }
        if (success)
        {
            await RunAsync(RefreshDemoScoresIfNeededAsync, "Risk estimates updated.");
            ShowExpiry();
        }
    }

    private sealed record ExpiryRow(string Name, FoodCategory Category, int Quantity,
        DateTime ExpiresOn, string Status, ExpiryStatus DateStatus, int DaysLeft, string SimulatedRisk);
    private sealed record BarRow(string Label, double Percentage);
}
