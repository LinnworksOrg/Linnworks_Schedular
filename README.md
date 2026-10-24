# Linnworks Daily Seed

Windows Service that seeds Linnworks test data on a schedule (default 09:00 India Standard Time).

See [PLAN.md](PLAN.md) for behaviour, config, and install steps.

```powershell
cd LinnworksDailySeed
# Fill appsettings.Local.json first
dotnet run -- --once
```
