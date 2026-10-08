# FreshTrack

FreshTrack is a Windows desktop application for household food inventory, expiry tracking, shopping, and food use/waste reports. It was built for UTS 31927/32998 Spring 2026 Assignment 2.

The four tabs use a resizable layout: Inventory and Shopping list place records beside their action forms, Expiry dashboard highlights status totals, and Reports uses summary cards with a scrollable analysis page.

## Requirements

- Windows 10/11.
- Visual Studio 2022 **17.12 or later** with the .NET desktop development workload, or the .NET 9 SDK and desktop runtime.
- Internet access on the first build to restore NuGet packages.

The solution targets `net9.0` and `net9.0-windows`. Close any running FreshTrack instance before rebuilding Debug, because Windows locks its loaded DLLs. Open `FreshTrack.sln` in Visual Studio, set `FreshTrack.App` as the startup project, restore packages, then press Ctrl+F5 (run without debugging) or F5 (debug). CLI alternatives:

```powershell
dotnet restore FreshTrack.sln --configfile NuGet.Config
dotnet build FreshTrack.sln --no-restore
dotnet run --project FreshTrack.App
dotnet test FreshTrack.sln --no-build
```

This workspace also contains a local `.dotnet9` SDK used during verification. If the global `dotnet` command still resolves to .NET 8 here, use the following PowerShell commands from the solution directory:

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location).Path '.dotnet9'
$env:DOTNET_CLI_HOME = (Get-Location).Path
$env:NUGET_PACKAGES = Join-Path (Get-Location).Path '.packages'
& .\.dotnet9\dotnet.exe run --project FreshTrack.App
```

The application creates `%LOCALAPPDATA%\FreshTrack\freshtrack.db` on first start. `DemoData.InitializeAsync` adds five food rows and two shopping rows **only when the database schema is first created**. Later launches keep user changes. The database is a local SQLite file; it is not included in the solution. To reset a demonstration, close the app, delete that database file, and restart. Do not delete a real user's database without a backup.

## Demonstration route

1. **Inventory:** search for `Milk`; select a row to fill the edit form. Add a food with name, category, positive whole-number quantity, purchase date, and expiry date. Select it to edit or delete. Record part of its quantity as Used or Wasted; history remains visible in the inventory grid.
   Repeated records for the same food details, outcome, and day appear as one history row with their quantities added. Existing same-day duplicates from earlier versions are consolidated when the application starts.
2. **Expiry dashboard:** the three priority groups are **Expired**, **High risk**, and **Lower risk**. Expired is determined by the actual date. For nonexpired stock, the trained model's simulated waste score is compared with a calibrated threshold (about 59% in one test run). Use the filter for either priority or the factual **Date: Soon/Fresh** status, and inspect the separate **Date status**, **Days left**, and **Simulated waste risk** columns. Date status still follows the category-specific rules: perishable categories are Soon within three days; others within seven days. A far-dated item may be High risk; a near-dated item may be Lower risk. Neither risk label is a food-safety judgement. On first run, the app prepares the synthetic model for this dashboard; the Reports button retrains it explicitly.
3. **Shopping list:** add an item, then tick its **Purchased** checkbox. This records the purchase date but does not add inventory. Select that purchased row, choose an expiry date, and click **Add selected purchase to inventory**. The food is added to Inventory and its shopping row disappears in one database save. Previously transferred shopping rows are removed on startup; purchased rows still awaiting transfer remain.
4. **Reports:** inspect current stock, used and wasted quantities by category and month, plus the waste bar chart. Click **Train demo model** to regenerate 40,000 synthetic training rows and 10,000 separately seeded test rows, train an ML.NET logistic regression classifier, recalibrate its priority threshold, and see each current in-stock food's days left, rule-based expiry status, and simulated waste score. A second grid summarizes these scores by category. For example, a meat item added today with tomorrow's expiry has Date status **Soon** and, in the tested model, priority **High risk**. After training, changes to inventory automatically rescore current stock with the saved model. This is a teaching simulation: its labels are generated, so the displayed probabilities are not validated household waste forecasts.

The machine-learning demo writes `synthetic_training.csv`, `synthetic_test.csv`, `waste_demo_model_v2.zip`, and `waste_demo_threshold_v2.txt` under `%LOCALAPPDATA%\FreshTrack\ml-demo`. It does not add synthetic rows to `freshtrack.db`. Repeating the button regenerates the same seeded datasets and overwrites the model and threshold. The generator uses illustrative household profiles such as one-day ground meat, milk, bread, produce, frozen foods, and dry goods; each row has a coherent shelf life, elapsed days since purchase, remaining days, and quantity. A simulated household consumption process decides whether any units remain unused at the entered expiry date. The model sees food category, shelf life, remaining days, within-three-days/expired flags, quantity, units per remaining day, and an Australian-summer indicator. A hidden planning factor varies the synthetic outcomes. These profiles and household behaviour rates are illustrative assumptions, **not** measured household data or universal food-safety limits. Follow actual product labels and storage instructions.

Threshold calibration uses an internal 80/20 split of the 40,000-row training file. A preliminary model is fitted on about 32,000 rows; about 8,000 rows choose a threshold between 50% and 85% that maximizes F1 while reaching at least 80% precision on that calibration split. The final model is then fitted on all 40,000 rows. The separate 10,000-row test file is used only for final AUC, F1, precision, and recall; it does not select the threshold. The on-screen threshold and metrics can vary slightly across runs. **Soon/Expired** in the Date status column are computed from dates and category policy; **High risk/Lower risk** are model-based priorities, not predictions that the calendar expiry date will change.

The short-life meat, poultry, leftovers and egg examples were informed by the [FoodSafety.gov cold storage chart](https://www.foodsafety.gov/food-safety-charts/cold-food-storage-charts). The [Food Standards Australia New Zealand date-label guidance](https://www.foodstandards.gov.au/consumer/labelling/dates) distinguishes `use-by` from `best-before`; FreshTrack currently has one expiry-date field and does not make that safety distinction. The synthetic category frequencies, age and quantity mix, consumption rates, and household planning factor are explicit developer assumptions, not estimates from those sources.

Validation requires nonempty names of at most 120 characters, positive whole-number quantities, a purchase date no later than today, and an expiry date no earlier than purchase. Errors appear in a dialog and status strip. Deletion of a food asks for confirmation.

## Solution structure

| Project | Purpose |
| --- | --- |
| `FreshTrack.App` | Four responsive WPF tabs and event handlers |
| `FreshTrack.Core` | Models, validation, expiry policies, EF Core context/repositories, business services and seed data |
| `FreshTrack.Tests` | NUnit tests of expiry rules, validation, CRUD, quantity transfer, reports and SQLite persistence |

NuGet dependencies: `Microsoft.EntityFrameworkCore.Sqlite` 9.0.9; `Microsoft.ML` 5.0.0; `NUnit` 4.3.2; `NUnit3TestAdapter` 5.0.0; `Microsoft.NET.Test.Sdk` 17.14.1. WPF is supplied by the Windows desktop SDK. `NuGet.Config` points to nuget.org.

## Verification completed on 7 October 2026

- .NET SDK 9.0.318: `dotnet build FreshTrack.sln -c Release --no-restore -m:1 -p:UseSharedCompilation=false` succeeded with **0 warnings and 0 errors**. Debug output was locked by an already running FreshTrack instance, so the current code was verified in the separate Release directory without closing that instance.
- `dotnet test FreshTrack.sln -c Release --no-build -m:1` reported **8 passed, 0 failed, 0 skipped**. Tests include expiry boundaries, validation, SQLite persistence, shopping transfer, repeated food-use consolidation, and ML.NET training/scoring.
- The revised ML test generated 40,000 training and 10,000 test rows, checked coherent dates/quantities and one-day meat examples, and loaded the saved model and threshold to score a newly added stock item. The training set contained **1,790 one-day meat rows** and had a **54.6% simulated unused-label rate**. A meat item bought today and expiring tomorrow had Date status **Soon** and model priority **High risk**; tests also distinguish far-dated high risk from near-dated lower risk. One held-out synthetic run selected a **59%** threshold and yielded AUC 0.850, F1 0.762, precision 0.800, recall 0.727; training may vary between runs. These metrics measure how well the classifier learns the simulator, not real-world accuracy.
- Earlier Windows GUI checks opened all four tabs, added Test Pears, used the old Expired filter, and observed report updates. The previous ML layout's training button ran successfully, but the **revised model-priority dashboard, threshold calibration, and automatic rescoring have not been manually exercised in the GUI**. The final two-step shopping transfer GUI sequence also needs a manual check.
- Visual Studio Community 2022 17.14.41 previously loaded and built all three projects and launched FreshTrack.App with Ctrl+F5. The final ZIP still needs testing on the assigned laboratory computer.

The local `.dotnet9`, `.packages`, `.dotnet`, `bin`, and `obj` directories are build tooling/cache and should be excluded from the final submission ZIP. Include the solution, source files, project files, `NuGet.Config`, `ReadMe.md`, `Project_Description.md`, `Report_Handoff.md`, and the group's final PDF.

The verification machine's local SQLite file now contains Tomatoes and Test Pears from the UI checks. A clean lab machine will receive only the initial sample rows.

## Known limits

FreshTrack has no external API, cloud sync, multi-user account system, or model trained on real household outcomes. The synthetic-data model is a genuine trained algorithm but has no demonstrated real-world predictive validity; any machine-learning bonus remains the tutor's decision. SQLite is a local database; no claim is made for the marking guide's separate “external database with LINQ” bonus. The first-run schema uses `EnsureCreated`; one explicit upgrade handles older shopping-transfer data, but future schema changes need their own migration strategy or a fresh demonstration database.

