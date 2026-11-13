# Linnworks Daily Seed

Seeds Linnworks test data every day using the scenario mix from `Linnworks_Seed_Scenarios_Catalog.xlsx`.

Shared logic lives in `LinnworksDailySeed.Core`. Two hosts call it:

| Host | Use |
|---|---|
| `LinnworksDailySeed` | Local / Windows Service (`dotnet run -- --once`) |
| `LinnworksDailySeed.Functions` | Azure Functions timer (09:00–12:00 IST) |

See [PLAN.md](PLAN.md) for the original Windows Service notes.

## Local one-shot (file run log)

```powershell
cd LinnworksDailySeed
# Fill appsettings.Local.json first
dotnet run -- --once
```

## Azure Function (blob run log)

1. Copy `LinnworksDailySeed.Functions/local.settings.json.example` to `local.settings.json` and fill Linnworks credentials.
2. Start Azurite (or set `AzureWebJobsStorage` to a real storage connection string).
3. Run:

```powershell
cd LinnworksDailySeed.Functions
func start
```

Default `SeedSchedule` is `0 30 3-6 * * *` (09:00, 10:00, 11:00, 12:00 IST). A completed day is skipped; a failed day resumes from the blob run log in container `seed-runs`.

### Deploy infrastructure (does not publish the code package)

```powershell
az group create -n rg-lwseed -l eastus
az deployment group create -g rg-lwseed -f infra/main.bicep `
  --parameters linnworksApplicationId=<guid> `
               linnworksApplicationSecret=<guid> `
               linnworksPermanentToken=<guid>
```

Then publish the Function App:

```powershell
cd LinnworksDailySeed.Functions
func azure functionapp publish <functionAppName>
```

Flex Consumption Bicep is under [infra/main.bicep](infra/main.bicep).
