using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Workflows;

public sealed class PurchaseOrderWorkflow
{
    private readonly LinnworksClient _client;
    private readonly RunLogStore _runLogStore;
    private readonly SeedOptions _options;
    private readonly ILogger<PurchaseOrderWorkflow> _logger;

    public PurchaseOrderWorkflow(
        LinnworksClient client,
        RunLogStore runLogStore,
        IOptions<SeedOptions> options,
        ILogger<PurchaseOrderWorkflow> logger)
    {
        _client = client;
        _runLogStore = runLogStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsurePurchaseOrdersAsync(
        DateOnly runDate,
        ResolvedCatalog catalog,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var qtyPerPo = Math.Max(5, _options.OrderCount);
        var prefix = string.IsNullOrWhiteSpace(run.ReferencePrefix)
            ? $"AUTO-{runDate:yyyyMMdd}"
            : run.ReferencePrefix;
        var rotation = _runLogStore.LoadRotation();
        var usedSuppliers = new HashSet<Guid>();
        var usedItems = new HashSet<Guid>();
        var supplierIndex = rotation.SupplierIndex;
        var purchaseItemIndex = rotation.PurchaseItemIndex;
        for (var i = 1; i <= _options.PurchaseOrderCount; i++)
        {
            var reference = $"{prefix}-PO-{i:00}";
            var existing = run.PurchaseOrders.FirstOrDefault(p => p.Reference == reference);
            if (existing is not null
                && string.Equals(existing.Status, "Delivered", StringComparison.OrdinalIgnoreCase)
                && existing.PurchaseId.HasValue)
            {
                _logger.LogInformation("Skipping existing delivered PO {Reference}", reference);
                continue;
            }

            existing ??= new PurchaseOrderRunItem { Reference = reference };
            if (!run.PurchaseOrders.Contains(existing))
            {
                run.PurchaseOrders.Add(existing);
            }

            try
            {
                var supplier = NextSupplier(catalog.Suppliers, ref supplierIndex, usedSuppliers);
                var stockItem = NextPurchaseItem(catalog.Items, ref purchaseItemIndex, usedItems);
                rotation.SupplierIndex = supplierIndex;
                rotation.PurchaseItemIndex = purchaseItemIndex;
                var currency = catalog.RateFor(supplier.Currency, supplierIndex);
                _runLogStore.SaveRotation(rotation);
                _logger.LogInformation(
                    "Creating purchase order {Reference} supplier={Supplier} sku={Sku} currency={Currency}",
                    reference,
                    supplier.SupplierName,
                    stockItem.ItemNumber,
                    currency.Currency);
                var purchaseId = await _client.CreatePurchaseOrderInitialAsync(
                    new CreatePurchaseOrderInitialParameter
                    {
                        fkSupplierId = supplier.pkSupplierID,
                        fkLocationId = catalog.Location.StockLocationId,
                        DateOfPurchase = DateTime.UtcNow.Date,
                        QuotedDeliveryDate = DateTime.UtcNow.Date.AddDays(1),
                        ExternalInvoiceNumber = reference,
                        PostagePaid = 0,
                        Currency = currency.Currency,
                        ShippingTaxRate = 0,
                        ConversionRate = currency.ConversionRate,
                        SupplierReferenceNumber = reference
                    },
                    cancellationToken).ConfigureAwait(false);

                existing.PurchaseId = purchaseId;

                await _client.AddPurchaseOrderItemAsync(
                    new AddPurchaseOrderItemParameter
                    {
                        pkPurchaseId = purchaseId,
                        fkStockItemId = stockItem.StockItemId,
                        Qty = qtyPerPo,
                        Cost = stockItem.PurchasePrice > 0 ? stockItem.PurchasePrice : 1,
                        TaxRate = stockItem.TaxRate,
                        PackQuantity = 1,
                        PackSize = 1,
                        Delivered = 0
                    },
                    cancellationToken).ConfigureAwait(false);

                await _client.ChangePurchaseOrderStatusAsync(
                    new ChangePurchaseOrderStatusParameter
                    {
                        pkPurchaseId = purchaseId,
                        status = "OPEN"
                    },
                    cancellationToken).ConfigureAwait(false);

                await _client.DeliverPurchaseItemAllAsync(purchaseId, cancellationToken).ConfigureAwait(false);

                stockItem.Available += qtyPerPo;
                existing.Status = "Delivered";
                existing.Error = null;
                _logger.LogInformation("Delivered purchase order {Reference} ({PurchaseId})", reference, purchaseId);
            }
            catch (Exception ex)
            {
                existing.Status = "Failed";
                existing.Error = ex.Message;
                run.Errors.Add($"PO {reference}: {ex.Message}");
                _logger.LogError(ex, "Failed purchase order {Reference}", reference);
                throw;
            }
        }

        _runLogStore.SaveRotation(rotation);
    }

    private static Supplier NextSupplier(IReadOnlyList<Supplier> suppliers, ref int index, HashSet<Guid> used)
    {
        for (var attempt = 0; attempt < suppliers.Count; attempt++)
        {
            var supplier = suppliers[index % suppliers.Count];
            index++;
            if (used.Add(supplier.pkSupplierID))
            {
                return supplier;
            }
        }

        return suppliers[index++ % suppliers.Count];
    }

    private static StockItem NextPurchaseItem(IReadOnlyList<StockItem> items, ref int index, HashSet<Guid> used)
    {
        for (var attempt = 0; attempt < items.Count; attempt++)
        {
            var item = items[index % items.Count];
            index++;
            if (used.Add(item.StockItemId))
            {
                return item;
            }
        }

        throw new InvalidOperationException("No unused stock item is left for another purchase order.");
    }
}
