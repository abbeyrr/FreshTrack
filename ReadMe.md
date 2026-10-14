# FreshTrack

FreshTrack is a Windows desktop application for managing household food. It keeps an inventory, highlights food that needs attention, turns shopping plans into inventory records, and summarises food use and waste. The application was built for UTS 31927/32998 Spring 2026 Assignment 2.

## Requirements

- Windows 10 or 11
- .NET 9 SDK and Windows Desktop Runtime
- Visual Studio 2022 version 17.12 or later with the **.NET desktop development** workload, if using Visual Studio
- Internet access for the first NuGet package restore

## Run the application

Open `FreshTrack.sln` in Visual Studio, set `FreshTrack.App` as the startup project, then press **F5**. From PowerShell in the solution directory, run:

```powershell
dotnet restore FreshTrack.sln --configfile NuGet.Config
dotnet run --project FreshTrack.App
```

To build and test the whole solution:

```powershell
dotnet build FreshTrack.sln -c Release
dotnet test FreshTrack.sln -c Release --no-build
```

## Use FreshTrack

1. **Inventory:** add food with a name, category, quantity, purchase date and expiry date. Select a row to edit or delete it. Search by name, and record any quantity as **Used** or **Wasted**. The remaining quantity stays in stock. Repeated use or waste entries for the same food and day are combined.
2. **Expiry dashboard:** review **Expired**, **High waste risk** and **Low waste risk** groups. The table also shows each food's expiry date, days left and date status. Use the status filter to focus on a group. **Update risk estimates** retrains the model and refreshes the scores.
3. **Shopping list:** add a planned purchase, tick **Purchased**, select the row, and choose an expiry date. **Add selected purchase to inventory** creates the food record and removes the shopping item.
4. **Reports:** view available and expired quantities, food marked as used or wasted, monthly activity, and waste by category. **Refresh reports** reads the latest inventory data.

Expiry status follows the entered date: a past date is **Expired**; otherwise food is **Soon** or **Fresh** according to its category. Risk scores help prioritise unexpired food. The ML.NET model is trained on 40,000 seeded synthetic household records and evaluated on a separate 10,000-record synthetic test set. It is created automatically when needed and saved under `%LOCALAPPDATA%\FreshTrack\ml-demo` for later scoring.

## Data and validation

Entity Framework Core stores food and shopping records in `%LOCALAPPDATA%\FreshTrack\freshtrack.db`, a local SQLite database. A new database receives sample food and shopping records. Later launches keep the user's changes.

Food names must be present and at most 120 characters. Quantities must be positive whole numbers; purchase dates cannot be in the future, and expiry dates cannot precede purchase dates. The app displays validation errors in a dialog and status bar.

## Solution structure

| Project | Contents |
| --- | --- |
| `FreshTrack.App` | WPF interface and event handlers |
| `FreshTrack.Core` | Models, expiry rules, EF Core storage, inventory and shopping services, reports, and ML.NET training |
| `FreshTrack.Tests` | NUnit tests for validation, expiry boundaries, stock changes, persistence, reports, and model scoring |

The projects target `net9.0` and `net9.0-windows`. NuGet dependencies are `Microsoft.EntityFrameworkCore.Sqlite` 9.0.9, `Microsoft.ML` 5.0.0, `NUnit` 4.3.2, `NUnit3TestAdapter` 5.0.0 and `Microsoft.NET.Test.Sdk` 17.14.1.

## Verification

On 14 October 2026, the Release build completed with **0 warnings and 0 errors**, and all **9 NUnit tests passed**.
