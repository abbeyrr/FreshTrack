using System.Globalization;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace FreshTrack.Core;

// This model is trained only on reproducible simulated observations. It is a
// teaching demonstration, not a validated prediction of household behaviour.
public sealed class WasteRiskDemoService
{
    public const int TrainingCount = 40_000;
    public const int TestCount = 10_000;
    public const int TrainingSeed = 31927;
    public const int TestSeed = 32998;
    private const string ModelFileName = "waste_demo_model_v2.zip";
    private const string ThresholdFileName = "waste_demo_threshold_v2.txt";

    // Illustrative household profiles. Actual package dates and storage
    // instructions take precedence; these are not food-safety rules.
    private static readonly IReadOnlyDictionary<FoodCategory, SyntheticProfile[]> Profiles =
        new Dictionary<FoodCategory, SyntheticProfile[]>
        {
            [FoodCategory.Produce] = [new("Leafy greens", 1, 5), new("Berries", 2, 6),
                new("Ripe fruit", 1, 7), new("Apples", 7, 21), new("Root vegetables", 10, 30)],
            [FoodCategory.Dairy] = [new("Opened milk", 1, 5), new("Milk", 5, 12),
                new("Yoghurt", 7, 21), new("Soft cheese", 3, 10), new("Hard cheese", 14, 45)],
            [FoodCategory.Meat] = [new("Ground meat", 1, 2), new("Fresh poultry", 1, 2),
                new("Fresh cuts", 3, 5), new("Cooked leftovers", 3, 4), new("Raw sausage", 1, 2)],
            [FoodCategory.Bakery] = [new("Fresh bread", 2, 7), new("Pastry", 1, 3),
                new("Packaged bread", 5, 14)],
            [FoodCategory.Pantry] = [new("Dry pasta", 90, 365), new("Rice", 90, 365),
                new("Cereal", 30, 180), new("Flour", 30, 180)],
            [FoodCategory.Frozen] = [new("Frozen vegetables", 30, 180),
                new("Frozen meat", 30, 120), new("Frozen meal", 30, 90)],
            [FoodCategory.Other] = [new("Eggs", 21, 35),
                new("Prepared salad", 3, 4), new("Sauce", 7, 90)]
        };

    public DemoRiskReport TrainAndPredict(IReadOnlyList<FoodItem> foods, string outputDirectory,
        DateTime? asOf = null)
    {
        DateTime today = (asOf ?? DateTime.Today).Date;
        Directory.CreateDirectory(outputDirectory);
        string trainingPath = Path.Combine(outputDirectory, "synthetic_training.csv");
        string testPath = Path.Combine(outputDirectory, "synthetic_test.csv");
        string modelPath = Path.Combine(outputDirectory, ModelFileName);
        string thresholdPath = Path.Combine(outputDirectory, ThresholdFileName);
        WriteCsv(trainingPath, Generate(TrainingCount, TrainingSeed));
        WriteCsv(testPath, Generate(TestCount, TestSeed));

        var ml = new MLContext(seed: TrainingSeed);
        IDataView training = ml.Data.LoadFromTextFile<WasteTrainingRow>(trainingPath, separatorChar: ',', hasHeader: true);
        IDataView test = ml.Data.LoadFromTextFile<WasteTrainingRow>(testPath, separatorChar: ',', hasHeader: true);
        var pipeline = ml.Transforms.Categorical.OneHotEncoding("CategoryEncoded", nameof(WasteTrainingRow.Category))
            .Append(ml.Transforms.Concatenate("Features", "CategoryEncoded",
                nameof(WasteTrainingRow.ShelfLifeDays), nameof(WasteTrainingRow.Summer),
                nameof(WasteTrainingRow.DaysUntilExpiry), nameof(WasteTrainingRow.Urgent),
                nameof(WasteTrainingRow.Expired), nameof(WasteTrainingRow.Quantity),
                nameof(WasteTrainingRow.UnitsPerDayNeeded)))
            .Append(ml.Transforms.NormalizeMinMax("Features"))
            .Append(ml.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName: nameof(WasteTrainingRow.Label), featureColumnName: "Features"));
        var split = ml.Data.TrainTestSplit(training, testFraction: 0.2, seed: TrainingSeed);
        ITransformer calibrationModel = pipeline.Fit(split.TrainSet);
        var calibrationRows = ml.Data.CreateEnumerable<WasteCalibrationRow>(
            calibrationModel.Transform(split.TestSet), reuseRowObject: false).ToList();
        float threshold = SelectThreshold(calibrationRows);

        ITransformer model = pipeline.Fit(training);
        var testScores = model.Transform(test);
        double auc = ml.BinaryClassification.Evaluate(testScores,
            labelColumnName: nameof(WasteTrainingRow.Label)).AreaUnderRocCurve;
        var heldOutRows = ml.Data.CreateEnumerable<WasteCalibrationRow>(
            testScores, reuseRowObject: false).ToList();
        ThresholdMetrics metrics = EvaluateThreshold(heldOutRows, threshold);
        ml.Model.Save(model, training.Schema, modelPath);
        File.WriteAllText(thresholdPath, threshold.ToString("R", CultureInfo.InvariantCulture));

        RiskScores scores = Score(ml, model, foods, today, threshold);
        return new DemoRiskReport(TrainingCount, TestCount, threshold, auc,
            metrics.F1, metrics.Precision, metrics.Recall,
            scores.Categories, scores.Items, outputDirectory);
    }

    public bool HasSavedModel(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, ModelFileName)) &&
        File.Exists(Path.Combine(outputDirectory, ThresholdFileName));

    public RiskScores ScoreSavedModel(IReadOnlyList<FoodItem> foods, string outputDirectory,
        DateTime? asOf = null)
    {
        string modelPath = Path.Combine(outputDirectory, ModelFileName);
        string thresholdPath = Path.Combine(outputDirectory, ThresholdFileName);
        var ml = new MLContext(seed: TrainingSeed);
        ITransformer model = ml.Model.Load(modelPath, out _);
        float threshold = float.Parse(File.ReadAllText(thresholdPath), CultureInfo.InvariantCulture);
        if (threshold is < 0 or > 1 || float.IsNaN(threshold))
            throw new InvalidDataException("The saved model threshold is invalid.");
        return Score(ml, model, foods, (asOf ?? DateTime.Today).Date, threshold);
    }

    private static RiskScores Score(MLContext ml, ITransformer model,
        IReadOnlyList<FoodItem> foods, DateTime today, float threshold)
    {
        var stockItems = foods.Where(x => x.State == FoodState.InStock).ToList();
        var eligibleItems = stockItems.Where(x => x.ExpiryStatus(today) != ExpiryStatus.Expired).ToList();
        var current = eligibleItems
            .Select(x => new WasteTrainingRow
            {
                Category = x.Category.ToString(),
                ShelfLifeDays = Math.Max(1, (float)(x.ExpiresOn.Date - x.PurchasedOn.Date).TotalDays),
                Summer = IsAustralianSummer(x.PurchasedOn.Month) ? 1f : 0f,
                DaysUntilExpiry = (x.ExpiresOn.Date - today).Days,
                Urgent = (x.ExpiresOn.Date - today).Days <= 3 ? 1f : 0f,
                Expired = x.ExpiresOn.Date < today ? 1f : 0f,
                Quantity = x.Quantity,
                UnitsPerDayNeeded = UnitsPerDayNeeded(x.Quantity, (x.ExpiresOn.Date - today).Days)
            }).ToList();
        var scored = current.Count == 0
            ? new List<WasteScoredRow>()
            : ml.Data.CreateEnumerable<WasteScoredRow>(
                model.Transform(ml.Data.LoadFromEnumerable(current)), reuseRowObject: false).ToList();
        if (scored.Count != eligibleItems.Count)
            throw new InvalidOperationException("The model did not return a score for every unexpired food item.");
        var probabilities = eligibleItems.Zip(scored, (food, score) => (Food: food, score.Probability))
            .ToDictionary(x => x.Food, x => (double)x.Probability);
        var items = stockItems.Select(food => new FoodRiskRow(
                food.Id, food.Name, food.Category, food.Quantity, food.ExpiresOn.Date,
                (food.ExpiresOn.Date - today).Days, food.ExpiryStatus(today),
                probabilities.TryGetValue(food, out double probability) ? probability : null))
            .OrderBy(x => x.DaysLeft).ThenByDescending(x => x.Probability).ToList();
        var grouped = items.Where(x => x.Probability.HasValue).GroupBy(x => x.Category)
            .Select(group => new CategoryRiskRow(
                group.Key, group.Count(), group.Average(x => x.Probability!.Value),
                group.Count(x => x.Probability!.Value >= threshold)))
            .OrderByDescending(x => x.AverageProbability).ToList();
        return new RiskScores(items, grouped, threshold);
    }

    private static float SelectThreshold(IReadOnlyList<WasteCalibrationRow> rows)
    {
        float bestThreshold = 0.6f;
        double bestF1 = -1;
        // Calibration uses only a split of the 40,000 training records. Keep
        // the separate 10,000-row test set untouched until final evaluation.
        for (int percent = 50; percent <= 85; percent++)
        {
            float candidate = percent / 100f;
            ThresholdMetrics result = EvaluateThreshold(rows, candidate);
            if (result.Precision < 0.80 || result.F1 <= bestF1) continue;
            bestThreshold = candidate;
            bestF1 = result.F1;
        }
        if (bestF1 < 0)
            throw new InvalidOperationException("No calibration threshold met the 80% precision target.");
        return bestThreshold;
    }

    private static ThresholdMetrics EvaluateThreshold(IEnumerable<WasteCalibrationRow> rows,
        float threshold)
    {
        int truePositive = 0, falsePositive = 0, falseNegative = 0;
        foreach (var row in rows)
        {
            bool flagged = row.Probability >= threshold;
            if (flagged && row.Label) truePositive++;
            else if (flagged) falsePositive++;
            else if (row.Label) falseNegative++;
        }
        double precision = truePositive + falsePositive == 0 ? 0 :
            (double)truePositive / (truePositive + falsePositive);
        double recall = truePositive + falseNegative == 0 ? 0 :
            (double)truePositive / (truePositive + falseNegative);
        double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new ThresholdMetrics(f1, precision, recall);
    }

    public static List<WasteTrainingRow> Generate(int count, int seed)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var random = new Random(seed);
        FoodCategory[] categories = Enum.GetValues<FoodCategory>();
        var rows = new List<WasteTrainingRow>(count);
        for (int i = 0; i < count; i++)
        {
            FoodCategory category = ChooseCategory(random, categories);
            SyntheticProfile[] choices = Profiles[category];
            SyntheticProfile profile = choices[random.Next(choices.Length)];
            int month = random.Next(1, 13);
            int shelfLife = random.Next(profile.MinimumDays, profile.MaximumDays + 1);
            int ageDays = SampleAge(random, shelfLife);
            int daysLeft = shelfLife - ageDays;
            int quantity = SampleQuantity(random);
            float urgent = daysLeft <= 3 ? 1f : 0f;
            float expired = daysLeft < 0 ? 1f : 0f;
            float summer = IsAustralianSummer(month) ? 1f : 0f;
            // Simulate whether household consumption leaves any units unused
            // by the entered date. A hidden planning factor creates variation
            // the classifier cannot simply read from a fixed label equation.
            bool wasted = SimulateUnusedAtDate(random, category, quantity, daysLeft, summer > 0);
            rows.Add(new WasteTrainingRow
            {
                Category = category.ToString(), ShelfLifeDays = shelfLife,
                Summer = summer, DaysUntilExpiry = daysLeft, Urgent = urgent,
                Expired = expired, Quantity = quantity,
                UnitsPerDayNeeded = UnitsPerDayNeeded(quantity, daysLeft),
                Profile = profile.Name, AgeDays = ageDays, Label = wasted
            });
        }
        return rows;
    }

    private static bool IsAustralianSummer(int month) => month is 12 or 1 or 2;

    private static FoodCategory ChooseCategory(Random random, FoodCategory[] categories)
    {
        double draw = random.NextDouble();
        double[] cumulative = [0.28, 0.46, 0.61, 0.73, 0.87, 0.95, 1.0];
        for (int i = 0; i < cumulative.Length; i++)
            if (draw < cumulative[i]) return categories[i];
        return categories[^1];
    }

    private static int SampleAge(Random random, int shelfLife)
    {
        double stage = random.NextDouble();
        if (stage < 0.55) return random.Next(0, shelfLife / 2 + 1);
        if (stage < 0.80) return random.Next(shelfLife / 2, shelfLife + 1);
        if (stage < 0.95) return random.Next(Math.Max(0, shelfLife - 2), shelfLife + 1);
        return shelfLife + random.Next(1, 4);
    }

    private static int SampleQuantity(Random random)
    {
        double draw = random.NextDouble();
        return draw < 0.52 ? 1 : draw < 0.78 ? 2 : draw < 0.91 ? 3 : random.Next(4, 9);
    }

    private static float UnitsPerDayNeeded(int quantity, int daysLeft) =>
        Math.Min(8f, quantity / (float)(Math.Max(0, daysLeft) + 1));

    private static bool SimulateUnusedAtDate(Random random, FoodCategory category,
        int quantity, int daysLeft, bool summer)
    {
        if (daysLeft < 0)
        {
            double unusedChance = category switch
            {
                FoodCategory.Produce or FoodCategory.Meat or FoodCategory.Dairy => 0.78,
                FoodCategory.Bakery => 0.68,
                FoodCategory.Pantry or FoodCategory.Frozen => 0.38,
                _ => 0.55
            };
            return random.NextDouble() < Math.Min(0.97, unusedChance + 0.025 * (quantity - 1));
        }

        double dailyUse = category switch
        {
            FoodCategory.Produce => 0.22,
            FoodCategory.Dairy => 0.20,
            FoodCategory.Meat => 0.16,
            FoodCategory.Bakery => 0.21,
            FoodCategory.Pantry => 0.025,
            FoodCategory.Frozen => 0.035,
            _ => 0.08
        };
        double planningFactor = 0.65 + 0.8 * random.NextDouble();
        dailyUse = Math.Clamp(dailyUse * planningFactor, 0.005, 0.65);
        double chanceOneUnitRemains = Math.Pow(1 - dailyUse, daysLeft + 1);
        double chanceAnyUnused = 1 - Math.Pow(1 - chanceOneUnitRemains, quantity);
        if (summer && category is FoodCategory.Produce or FoodCategory.Meat or FoodCategory.Dairy)
            chanceAnyUnused = Math.Min(0.98, chanceAnyUnused + 0.025);
        return random.NextDouble() < chanceAnyUnused;
    }

    private static void WriteCsv(string path, IEnumerable<WasteTrainingRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("Label,Category,ShelfLifeDays,Summer,DaysUntilExpiry,Urgent,Expired,Quantity,UnitsPerDayNeeded,Profile,AgeDays");
        foreach (var row in rows)
            writer.WriteLine(string.Join(',',
                row.Label ? "true" : "false", row.Category,
                row.ShelfLifeDays.ToString(CultureInfo.InvariantCulture),
                row.Summer.ToString(CultureInfo.InvariantCulture),
                row.DaysUntilExpiry.ToString(CultureInfo.InvariantCulture),
                row.Urgent.ToString(CultureInfo.InvariantCulture),
                row.Expired.ToString(CultureInfo.InvariantCulture),
                row.Quantity.ToString(CultureInfo.InvariantCulture),
                row.UnitsPerDayNeeded.ToString(CultureInfo.InvariantCulture),
                row.Profile, row.AgeDays.ToString(CultureInfo.InvariantCulture)));
    }

    private sealed record SyntheticProfile(string Name, int MinimumDays, int MaximumDays);
}

public sealed class WasteTrainingRow
{
    [LoadColumn(0)] public bool Label { get; set; }
    [LoadColumn(1)] public string Category { get; set; } = string.Empty;
    [LoadColumn(2)] public float ShelfLifeDays { get; set; }
    [LoadColumn(3)] public float Summer { get; set; }
    [LoadColumn(4)] public float DaysUntilExpiry { get; set; }
    [LoadColumn(5)] public float Urgent { get; set; }
    [LoadColumn(6)] public float Expired { get; set; }
    [LoadColumn(7)] public float Quantity { get; set; }
    [LoadColumn(8)] public float UnitsPerDayNeeded { get; set; }
    [LoadColumn(9)] public string Profile { get; set; } = string.Empty;
    [LoadColumn(10)] public float AgeDays { get; set; }
}

public sealed class WasteScoredRow
{
    [ColumnName("Probability")] public float Probability { get; set; }
}

public sealed class WasteCalibrationRow
{
    public bool Label { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
}

public sealed record FoodRiskRow(int FoodId, string Name, FoodCategory Category, int Quantity,
    DateTime ExpiresOn, int DaysLeft, ExpiryStatus Status, double? Probability)
{
    public string SimulatedRisk => Probability is double value
        ? (value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
    public string AttentionStatus(float threshold) => Status == ExpiryStatus.Expired
        ? "Expired" : Probability is null ? "Unscored" :
        Probability >= threshold ? "High risk" : "Lower risk";
}

public sealed record CategoryRiskRow(FoodCategory Category, int StockItems,
    double AverageProbability, int HighRiskItems)
{
    public string AverageRisk => (AverageProbability * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
}

public sealed record DemoRiskReport(int TrainingRows, int TestRows, float Threshold, double Auc,
    double F1, double Precision, double Recall, IReadOnlyList<CategoryRiskRow> Categories,
    IReadOnlyList<FoodRiskRow> Items, string OutputDirectory);

public sealed record RiskScores(IReadOnlyList<FoodRiskRow> Items,
    IReadOnlyList<CategoryRiskRow> Categories, float Threshold);

internal sealed record ThresholdMetrics(double F1, double Precision, double Recall);
