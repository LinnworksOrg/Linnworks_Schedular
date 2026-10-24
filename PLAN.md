# Linnworks daily seed service

New project at this folder (`LinnworksDailySeed`). It does not reference SyncTools / `LinnWorkIntegration.sln`.

> **Path note:** `D:\Linnworks_Schedular` was read-only for the build account (Users = ReadAndExecute only). The project was created at `D:\Old-Synctools\Linnworks_Schedular`. After granting write access to `D:\Linnworks_Schedular`, copy this folder there.

## Config instead of Web.config

This is a Windows Service, so there is no `Web.config`. Edit:

- `LinnworksDailySeed/appsettings.json` — schedule and volumes
- `LinnworksDailySeed/appsettings.Local.json` — secrets (gitignored; copy from `appsettings.Local.json.example`)

```json
{
  "Seed": {
    "TimeZone": "India Standard Time",
    "RunAt": "09:00",
    "PurchaseOrderCount": 1,
    "OrderCount": 1,
    "RefundCount": 1
  }
}
```

`RunAt` is `HH:mm` in that time zone. `RefundCount` cannot exceed `OrderCount`. Raising counts to 10 / 50 / 10 later needs no code change.

`appsettings.Local.json`:

```json
{
  "Linnworks": {
    "ApplicationId": "<guid>",
    "ApplicationSecret": "<guid>",
    "PermanentToken": "<guid>"
  }
}
```

Optional overrides (leave empty for auto-pick): `LocationName`, `SupplierName`, `PaymentMethodName`, `PostalServiceName`.

## Behaviour

1. `Auth/AuthorizeByApplication` with the permanent token → session `Token` + `Server`
2. Resolve catalog (exactly one non-fulfilment location / supplier / payment method / postal service, else stop and ask for a name in config; one non-composite SKU)
3. Create `PurchaseOrderCount` POs: PENDING → add line → OPEN → `Deliver_PurchaseItemAll`
4. Create `OrderCount` paid `DIRECT` / `AUTO-SEED` orders → `ProcessOrder`
5. Create and action `RefundCount` refunds on the first processed orders
6. Write `runs/yyyy-MM-dd.json`; a completed day is skipped on the next start

References: `AUTO-yyyyMMdd-PO-01`, `AUTO-yyyyMMdd-ORD-01`.

## Manual test (before installing the service)

1. Fill `appsettings.Local.json` with the test-account System Integration app credentials.
2. From `LinnworksDailySeed`:

```powershell
dotnet run -- --once
```

3. In Linnworks confirm: delivered PO, processed DIRECT order, actioned refund. Log must name location, supplier, SKU.
4. Run `--once` again the same day; log should say the day is already completed.
5. Install only after that:

```powershell
dotnet publish -c Release -o C:\Services\LinnworksDailySeed
sc.exe create LinnworksDailySeed binPath= "C:\Services\LinnworksDailySeed\LinnworksDailySeed.exe" start= auto
sc.exe start LinnworksDailySeed
```

To prove the clock without waiting until 09:00, set `RunAt` a few minutes ahead, restart the service, watch logs, then set `RunAt` back to `09:00`.

## Grant write access to D:\Linnworks_Schedular (admin PowerShell)

```powershell
icacls "D:\Linnworks_Schedular" /grant "${env:USERNAME}:(OI)(CI)M"
robocopy "D:\Old-Synctools\Linnworks_Schedular" "D:\Linnworks_Schedular" /E
```
