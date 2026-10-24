using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Workflows;

public sealed class OrderWorkflow
{
    private readonly LinnworksClient _client;
    private readonly RunLogStore _runLogStore;
    private readonly SeedOptions _options;
    private readonly ILogger<OrderWorkflow> _logger;

    public OrderWorkflow(
        LinnworksClient client,
        RunLogStore runLogStore,
        IOptions<SeedOptions> options,
        ILogger<OrderWorkflow> logger)
    {
        _client = client;
        _runLogStore = runLogStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureOrdersAsync(
        DateOnly runDate,
        ResolvedCatalog catalog,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var toCreate = new List<(OrderRunItem Item, ChannelOrder Payload, bool ClearSubSource)>();
        var prefix = string.IsNullOrWhiteSpace(run.ReferencePrefix)
            ? $"AUTO-{runDate:yyyyMMdd}"
            : run.ReferencePrefix;
        var remaining = catalog.Items.ToDictionary(i => i.StockItemId, i => i.Available);
        var usedItems = new HashSet<Guid>();
        var usedCustomers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rotation = _runLogStore.LoadRotation();
        var customerIndex = rotation.CustomerIndex;
        var orderItemIndex = rotation.OrderItemIndex;

        for (var i = 1; i <= _options.OrderCount; i++)
        {
            var reference = $"{prefix}-ORD-{i:00}";
            var existing = run.Orders.FirstOrDefault(o => o.Reference == reference);
            if (existing is not null
                && existing.Processed
                && existing.OrderId.HasValue)
            {
                _logger.LogInformation("Skipping existing processed order {Reference}", reference);
                continue;
            }

            existing ??= new OrderRunItem { Reference = reference };
            if (!run.Orders.Contains(existing))
            {
                run.Orders.Add(existing);
            }

            if (existing.OrderId.HasValue && !existing.Processed)
            {
                await ProcessExistingAsync(existing, catalog.Location.StockLocationId, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var customer = NextDistinct(
                catalog.Customers,
                ref customerIndex,
                usedCustomers,
                c => c.FullName);
            var stockItem = TakeFulfillableItem(catalog, remaining, usedItems, ref orderItemIndex);
            rotation.CustomerIndex = customerIndex;
            rotation.OrderItemIndex = orderItemIndex;
            var payment = catalog.PickPayment(customerIndex + i);
            var postal = catalog.PickPostal(orderItemIndex + i);
            var currency = catalog.PickCurrency(customerIndex + i);
            if (currency.ConversionRate <= 0)
            {
                currency = new CurrencyConversionRate { Currency = currency.Currency, ConversionRate = 1 };
            }

            var clearSubSource = i % 2 == 0;
            var subSource = catalog.PickSubSource(customerIndex + i);
            _logger.LogInformation(
                "Order {Reference} customer={Customer} sku={Sku} currency={Currency} subsource={SubSource} payment={Payment} postal={Postal}",
                reference,
                customer.FullName,
                stockItem.ItemNumber,
                currency.Currency,
                clearSubSource ? "(empty after create)" : subSource,
                payment.Name,
                postal.PostalServiceName);
            toCreate.Add((existing, BuildOrder(reference, customer, stockItem, payment, postal, currency, subSource), clearSubSource));
        }

        _runLogStore.SaveRotation(rotation);

        if (toCreate.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Creating {Count} orders via CreateOrders", toCreate.Count);
        List<Guid> createdIds;
        try
        {
            createdIds = await _client.CreateOrdersAsync(
                toCreate.Select(x => x.Payload).ToList(),
                catalog.Location.LocationName ?? "default",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var (item, _, _) in toCreate)
            {
                item.Status = "Failed";
                item.Error = ex.Message;
                run.Errors.Add($"Order {item.Reference}: {ex.Message}");
            }

            throw;
        }

        if (createdIds.Count != toCreate.Count)
        {
            var message =
                $"CreateOrders returned {createdIds.Count} ids for {toCreate.Count} payloads. Check duplicate Source/SubSource/ReferenceNumber.";
            run.Errors.Add(message);
            throw new InvalidOperationException(message);
        }

        for (var i = 0; i < toCreate.Count; i++)
        {
            var item = toCreate[i].Item;
            item.OrderId = createdIds[i];
            item.Status = "Created";
            if (toCreate[i].ClearSubSource)
            {
                try
                {
                    await _client.SetOrderSubSourceAsync(item.OrderId.Value, "", cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation("Cleared subsource on {Reference}", item.Reference);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Linnworks did not accept an empty subsource on {Reference}. The channel subsource was left in place.",
                        item.Reference);
                }
            }

            await ProcessExistingAsync(item, catalog.Location.StockLocationId, cancellationToken)
                .ConfigureAwait(false);
            if (!item.Processed)
            {
                run.Errors.Add($"Order {item.Reference}: {item.Error}");
                throw new InvalidOperationException($"Order {item.Reference} was not processed: {item.Error}");
            }
        }
    }

    private async Task ProcessExistingAsync(
        OrderRunItem item,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        if (!item.OrderId.HasValue)
        {
            throw new InvalidOperationException($"Order {item.Reference} has no OrderId to process.");
        }

        var orderIds = new List<Guid> { item.OrderId.Value };
        _logger.LogInformation(
            "Marking invoice and label printed for {Reference} ({OrderId})",
            item.Reference,
            item.OrderId);
        await _client.SetInvoicesPrintedAsync(orderIds, cancellationToken).ConfigureAwait(false);
        await _client.SetLabelsPrintedAsync(orderIds, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Processing order {Reference} ({OrderId})", item.Reference, item.OrderId);
        var result = await _client.ProcessOrderAsync(item.OrderId.Value, locationId, cancellationToken)
            .ConfigureAwait(false);

        item.Processed = result.Processed;
        item.Status = result.Processed ? "Processed" : "ProcessFailed";
        item.Error = result.Processed ? null : result.Error;
        if (!result.Processed)
        {
            _logger.LogError(
                "ProcessOrder failed for {Reference}: {Error}",
                item.Reference,
                result.Error);
        }
    }

    private static T NextDistinct<T>(
        IReadOnlyList<T> items,
        ref int index,
        HashSet<string> usedKeys,
        Func<T, string> key)
    {
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Nothing is available to rotate.");
        }

        for (var attempt = 0; attempt < items.Count; attempt++)
        {
            var item = items[index % items.Count];
            index++;
            if (usedKeys.Add(key(item)))
            {
                return item;
            }
        }

        var fallback = items[index % items.Count];
        index++;
        return fallback;
    }

    private static StockItem TakeFulfillableItem(
        ResolvedCatalog catalog,
        Dictionary<Guid, int> remaining,
        HashSet<Guid> usedItems,
        ref int index)
    {
        if (catalog.Items.Count == 0)
        {
            throw new InvalidOperationException(
                $"No stock item at '{catalog.Location.LocationName}' is available.");
        }

        for (var attempt = 0; attempt < catalog.Items.Count; attempt++)
        {
            var item = catalog.Items[index % catalog.Items.Count];
            index++;
            if (remaining.GetValueOrDefault(item.StockItemId) < 1)
            {
                continue;
            }

            if (!usedItems.Add(item.StockItemId))
            {
                continue;
            }

            remaining[item.StockItemId] = remaining.GetValueOrDefault(item.StockItemId) - 1;
            return item;
        }

        throw new InvalidOperationException(
            $"No stock item at '{catalog.Location.LocationName}' has available quantity left to fulfil another order.");
    }

    private ChannelOrder BuildOrder(
        string reference,
        SeedCustomer customer,
        StockItem stockItem,
        PaymentMethod payment,
        PostageService postal,
        CurrencyConversionRate currency,
        string subSource)
    {
        var now = DateTime.UtcNow;
        var unitPrice = stockItem.RetailPrice is > 0
            ? stockItem.RetailPrice.Value
            : stockItem.PurchasePrice > 0 ? stockItem.PurchasePrice : 10;

        return new ChannelOrder
        {
            Source = _options.Source,
            SubSource = subSource,
            ReferenceNumber = reference,
            ReceivedDate = now,
            DispatchBy = now.AddDays(7),
            PaidOn = now,
            PaymentStatus = ChannelPaymentStatus.Paid,
            OrderStatusType = ChannelOrderStatus.Unshipped,
            OrderStatus = "Unshipped",
            OrderState = ChannelOrderState.None,
            Currency = currency.Currency,
            ConversionRate = currency.ConversionRate,
            ChannelBuyerName = customer.FullName,
            MatchPaymentMethodTag = payment.Name,
            PaymentMethodName = payment.Name,
            SavePaymentMethodIfNotExist = false,
            MatchPostalServiceTag = postal.PostalServiceName,
            PostalServiceName = postal.PostalServiceName,
            SavePostalServiceIfNotExist = false,
            AutomaticallyLinkBySKU = true,
            UseChannelTax = true,
            BillingAddress = customer.Address,
            DeliveryAddress = customer.Address,
            Notes =
            [
                new ChannelOrderNote
                {
                    Note = "Created by LinnworksDailySeed",
                    NoteUserName = "LinnworksDailySeed"
                }
            ],
            OrderItems =
            [
                new ChannelOrderItem
                {
                    ChannelSKU = stockItem.ItemNumber,
                    ItemNumber = stockItem.ItemNumber,
                    ItemTitle = stockItem.ItemTitle ?? stockItem.ItemNumber,
                    Qty = 1,
                    PricePerUnit = unitPrice,
                    TaxRate = stockItem.TaxRate,
                    TaxCostInclusive = true,
                    UseChannelTax = true,
                    IsService = false,
                    LineDiscount = 0
                }
            ]
        };
    }
}
