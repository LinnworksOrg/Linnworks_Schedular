using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Scenarios;

namespace LinnworksDailySeed.Workflows;

public sealed class PurchaseOrderWorkflow
{
    private readonly LinnworksClient _client;
    private readonly IRunLogStore _runLogStore;
    private readonly ILogger<PurchaseOrderWorkflow> _logger;

    public PurchaseOrderWorkflow(
        LinnworksClient client,
        IRunLogStore runLogStore,
        ILogger<PurchaseOrderWorkflow> logger)
    {
        _client = client;
        _runLogStore = runLogStore;
        _logger = logger;
    }

    public async Task EnsurePurchaseOrdersAsync(
        DateOnly runDate,
        ResolvedCatalog catalog,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var prefix = string.IsNullOrWhiteSpace(run.ReferencePrefix)
            ? $"AUTO-{runDate:yyyyMMdd}"
            : run.ReferencePrefix;
        var rotation = _runLogStore.LoadRotation();

        foreach (var spec in DailyScenarioPlan.PurchaseOrders)
        {
            for (var i = 1; i <= spec.Count; i++)
            {
                var reference = $"{prefix}-{spec.ScenarioId}-{i:00}";
                var existing = run.PurchaseOrders.FirstOrDefault(p => p.Reference == reference);
                if (existing is not null && IsComplete(existing))
                {
                    _logger.LogInformation("Skipping existing {Scenario} {Reference}", spec.ScenarioId, reference);
                    continue;
                }

                existing ??= new PurchaseOrderRunItem
                {
                    ScenarioId = spec.ScenarioId,
                    AlsoCovers = "PO-PENDING, PO-ADD-LINE, PO-OPEN",
                    Reference = reference
                };
                existing.ScenarioId = spec.ScenarioId;
                existing.AlsoCovers = "PO-PENDING, PO-ADD-LINE, PO-OPEN";
                if (!run.PurchaseOrders.Contains(existing))
                {
                    run.PurchaseOrders.Add(existing);
                }

                try
                {
                    await CreateOneAsync(catalog, spec, existing, i, rotation, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    existing.Status = "Failed";
                    existing.Error = ex.Message;
                    run.Errors.Add($"PO {reference}: {ex.Message}");
                    _logger.LogError(ex, "Failed purchase order {Reference}", reference);
                }

                _runLogStore.SaveRotation(rotation);
                _runLogStore.Save(run);
            }
        }
    }

    private async Task CreateOneAsync(
        ResolvedCatalog catalog,
        PurchaseScenarioSpec spec,
        PurchaseOrderRunItem existing,
        int slot,
        RotationState rotation,
        CancellationToken cancellationToken)
    {
        var supplierIndex = rotation.SupplierIndex;
        var itemIndex = rotation.PurchaseItemIndex;
        var supplier = NextSupplier(catalog.Suppliers, ref supplierIndex);
        var currency = catalog.RateFor(supplier.Currency, supplierIndex);
        if (spec.ForeignCurrency)
        {
            var fx = catalog.NonBaseCurrency(slot)
                ?? throw new InvalidOperationException(
                    "PO-FX needs a non-base currency in Linnworks (a conversion rate other than 1).");
            currency = fx;
            var fxSupplier = catalog.Suppliers.FirstOrDefault(s =>
                string.Equals(s.Currency, fx.Currency, StringComparison.OrdinalIgnoreCase));
            if (fxSupplier is not null)
            {
                supplier = fxSupplier;
            }
        }

        var lines = TakeItems(catalog.Items, ref itemIndex, spec.LineCount);
        rotation.SupplierIndex = supplierIndex;
        rotation.PurchaseItemIndex = itemIndex;
        var postage = spec.Postage ? 15d : 0d;
        var shippingTax = spec.Postage ? 20d : 0d;
        _logger.LogInformation(
            "Creating {Scenario} {Reference} supplier={Supplier} skus={Skus} currency={Currency} qty={Qty}",
            spec.ScenarioId,
            existing.Reference,
            supplier.SupplierName,
            string.Join(",", lines.Select(l => l.ItemNumber)),
            currency.Currency,
            spec.Quantity);

        var purchaseId = await _client.CreatePurchaseOrderInitialAsync(
            new CreatePurchaseOrderInitialParameter
            {
                fkSupplierId = supplier.pkSupplierID,
                fkLocationId = catalog.Location.StockLocationId,
                DateOfPurchase = DateTime.UtcNow.Date,
                QuotedDeliveryDate = DateTime.UtcNow.Date.AddDays(1),
                ExternalInvoiceNumber = existing.Reference,
                PostagePaid = postage,
                Currency = currency.Currency,
                ShippingTaxRate = shippingTax,
                ConversionRate = currency.ConversionRate,
                SupplierReferenceNumber = existing.Reference
            },
            cancellationToken).ConfigureAwait(false);
        existing.PurchaseId = purchaseId;

        var added = new List<PurchaseOrderItem>();
        foreach (var stockItem in lines)
        {
            var response = await _client.AddPurchaseOrderItemAsync(
                new AddPurchaseOrderItemParameter
                {
                    pkPurchaseId = purchaseId,
                    fkStockItemId = stockItem.StockItemId,
                    Qty = spec.Quantity,
                    Cost = stockItem.PurchasePrice > 0 ? stockItem.PurchasePrice : 1,
                    TaxRate = stockItem.TaxRate,
                    PackQuantity = 1,
                    PackSize = 1,
                    Delivered = 0
                },
                cancellationToken).ConfigureAwait(false);

            var line = response.ResolvedItem;
            if (line is null || line.pkPurchaseItemId == Guid.Empty)
            {
                line = await ResolveAddedLineAsync(purchaseId, stockItem.StockItemId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (line is null || line.pkPurchaseItemId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Add_PurchaseOrderItem did not return a line id for {stockItem.ItemNumber}.");
            }

            added.Add(line);
        }

        if (spec.AdditionalCost)
        {
            await AddLandedCostAsync(purchaseId, existing.Reference, currency, cancellationToken)
                .ConfigureAwait(false);
        }

        await _client.ChangePurchaseOrderStatusAsync(
            new ChangePurchaseOrderStatusParameter
            {
                pkPurchaseId = purchaseId,
                status = "OPEN"
            },
            cancellationToken).ConfigureAwait(false);

        if (spec.Partial)
        {
            var deliver = Math.Min(spec.DeliverQuantity, spec.Quantity);
            string? status = null;
            foreach (var line in added)
            {
                var delivered = await _client.DeliverPurchaseItemAsync(
                    new DeliverPurchaseItemParameter
                    {
                        pkPurchaseId = purchaseId,
                        pkPurchaseItemId = line.pkPurchaseItemId,
                        Delivered = deliver
                    },
                    cancellationToken).ConfigureAwait(false);
                status = delivered.PurchaseOrderHeader?.Status ?? status;
            }

            if (!string.Equals(status, "PARTIAL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Partial delivery of {existing.Reference} left the purchase order '{status ?? "unknown"}' instead of PARTIAL.");
            }

            foreach (var stockItem in lines)
            {
                stockItem.Available += deliver;
            }

            existing.Status = "Partial";
        }
        else
        {
            await DeliverAllAsync(purchaseId, added, spec.Quantity, existing.Reference, cancellationToken)
                .ConfigureAwait(false);
            foreach (var stockItem in lines)
            {
                stockItem.Available += spec.Quantity;
            }

            existing.Status = "Delivered";
        }

        existing.Error = null;
        _logger.LogInformation(
            "Finished {Scenario} {Reference} ({PurchaseId}) status={Status}",
            spec.ScenarioId,
            existing.Reference,
            purchaseId,
            existing.Status);
    }

    private async Task DeliverAllAsync(
        Guid purchaseId,
        IReadOnlyList<PurchaseOrderItem> lines,
        int quantity,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeliverPurchaseItemAllAsync(purchaseId, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (LinnworksHttpException ex) when (RequiresBatchDelivery(ex))
        {
            _logger.LogWarning(
                ex,
                "Deliver_PurchaseItemAll blocked by batch tracking on {Reference}; delivering lines with seed batch numbers",
                reference);
        }

        var batchNumber = $"SEED-{DateTime.UtcNow:yyyyMMddHHmmss}";
        foreach (var line in lines)
        {
            await _client.DeliverPurchaseItemAsync(
                new DeliverPurchaseItemParameter
                {
                    pkPurchaseId = purchaseId,
                    pkPurchaseItemId = line.pkPurchaseItemId,
                    Delivered = quantity,
                    BatchNumber = batchNumber,
                    BatchStatus = "AVAILABLE",
                    PrioritySequence = 0
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool RequiresBatchDelivery(LinnworksHttpException ex)
        => ex.ResponseBody.Contains("Batch", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Batch", StringComparison.OrdinalIgnoreCase);

    private async Task<PurchaseOrderItem?> ResolveAddedLineAsync(
        Guid purchaseId,
        Guid stockItemId,
        CancellationToken cancellationToken)
    {
        var po = await _client.GetPurchaseOrderAsync(purchaseId, cancellationToken).ConfigureAwait(false);
        return po.PurchaseOrderItem?
            .FirstOrDefault(i => i.fkStockItemId == stockItemId && i.pkPurchaseItemId != Guid.Empty);
    }

    private async Task AddLandedCostAsync(
        Guid purchaseId,
        string reference,
        CurrencyConversionRate currency,
        CancellationToken cancellationToken)
    {
        var types = await _client.GetAdditionalCostTypesAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var costType = types.FirstOrDefault(t => t.AdditionalCostTypeId > 0)
            ?? throw new InvalidOperationException(
                "PO-ADDITIONAL-COST needs at least one additional cost type in Linnworks. Configure one under Purchase Orders settings.");
        _logger.LogInformation(
            "Adding additional cost type {TypeId} {TypeName} on {Reference}",
            costType.AdditionalCostTypeId,
            costType.TypeName,
            reference);
        await _client.ModifyPurchaseOrderAdditionalCostAsync(
            purchaseId,
            [
                new AddAdditionalCostItem
                {
                    AdditionalCostTypeId = costType.AdditionalCostTypeId,
                    Reference = reference,
                    SubTotalLineCost = 5,
                    TaxRate = 20,
                    Currency = currency.Currency,
                    ConversionRate = currency.ConversionRate <= 0 ? 1 : currency.ConversionRate,
                    AllocationMethod = string.IsNullOrWhiteSpace(costType.AllocationMethod)
                        ? "ByValue"
                        : costType.AllocationMethod
                }
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsComplete(PurchaseOrderRunItem item)
        => item.PurchaseId.HasValue
           && (item.Status.Equals("Delivered", StringComparison.OrdinalIgnoreCase)
               || item.Status.Equals("Partial", StringComparison.OrdinalIgnoreCase)
               || item.Status.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase));

    private static Supplier NextSupplier(IReadOnlyList<Supplier> suppliers, ref int index)
    {
        if (suppliers.Count == 0)
        {
            throw new InvalidOperationException("No supplier is available for a purchase order.");
        }

        var supplier = suppliers[index % suppliers.Count];
        index++;
        return supplier;
    }

    private static List<StockItem> TakeItems(IReadOnlyList<StockItem> items, ref int index, int count)
    {
        if (items.Count < count)
        {
            throw new InvalidOperationException(
                $"This purchase order needs {count} distinct SKUs. The location has {items.Count}.");
        }

        var picked = new List<StockItem>();
        var seen = new HashSet<Guid>();
        for (var attempt = 0; attempt < items.Count && picked.Count < count; attempt++)
        {
            var item = items[index % items.Count];
            index++;
            if (seen.Add(item.StockItemId))
            {
                picked.Add(item);
            }
        }

        if (picked.Count < count)
        {
            throw new InvalidOperationException(
                $"This purchase order needs {count} distinct SKUs. Could only pick {picked.Count}.");
        }

        return picked;
    }
}
