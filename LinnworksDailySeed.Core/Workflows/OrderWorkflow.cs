using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Scenarios;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Workflows;

public sealed class OrderWorkflow
{
    private const double ShippingCost = 4.99;
    private const double ShippingDiscount = 1.50;

    private static readonly (string Country, string Code, string Region, string Town, string PostCode)[] TaxCountries =
    [
        ("United States", "US", "California", "Los Angeles", "90001"),
        ("Germany", "DE", "Berlin", "Berlin", "10115"),
        ("France", "FR", "Ile-de-France", "Paris", "75001")
    ];

    private readonly LinnworksClient _client;
    private readonly IRunLogStore _runLogStore;
    private readonly SeedOptions _options;
    private readonly ILogger<OrderWorkflow> _logger;
    private int? _invoiceTemplateRowId;
    private bool _invoiceTemplateResolved;

    public OrderWorkflow(
        LinnworksClient client,
        IRunLogStore runLogStore,
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
        var prefix = string.IsNullOrWhiteSpace(run.ReferencePrefix)
            ? $"AUTO-{runDate:yyyyMMdd}"
            : run.ReferencePrefix;
        var rotation = _runLogStore.LoadRotation();
        var customerIndex = rotation.CustomerIndex;
        var orderItemIndex = rotation.OrderItemIndex;
        var compositeIndex = 0;
        var pool = new StockPool(catalog.Items, orderItemIndex);
        var usedCustomers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingCreate = new List<(OrderRunItem Item, ChannelOrder Payload)>();
        var specs = DailyScenarioPlan.Orders.Concat(DailyScenarioPlan.Cancels);

        foreach (var spec in specs)
        {
            for (var i = 1; i <= spec.Count; i++)
            {
                var reference = $"{prefix}-{spec.ScenarioId}-{i:00}";
                var existing = run.Orders.FirstOrDefault(o => o.Reference == reference);
                if (existing is not null && IsCreated(existing))
                {
                    _logger.LogInformation("Skipping existing order {Reference} status={Status}", reference, existing.Status);
                    continue;
                }

                existing ??= new OrderRunItem { Reference = reference };
                existing.ScenarioId = spec.ScenarioId;
                existing.ShouldProcess = spec.Process;
                existing.ClearSubSource = spec.ScenarioId == "ORD-BASE-PAID";
                existing.AlsoCovers = AlsoCovers(spec.ScenarioId, i);
                if (!run.Orders.Contains(existing))
                {
                    run.Orders.Add(existing);
                }

                if (existing.OrderId.HasValue)
                {
                    continue;
                }

                try
                {
                    var customer = NextDistinct(catalog.Customers, ref customerIndex, usedCustomers, c => c.FullName);
                    var shaped = Shape(catalog, pool, spec, i, reference, customer, ref compositeIndex, ref orderItemIndex);
                    existing.LineAmount = shaped.LineAmount;
                    existing.LineQty = shaped.LineQty;
                    existing.Postage = shaped.Postage;
                    _logger.LogInformation(
                        "Order {Reference} scenario={Scenario} also={Also} customer={Customer} postage={Postage}",
                        reference,
                        spec.ScenarioId,
                        existing.AlsoCovers,
                        customer.FullName,
                        shaped.Postage);
                    pendingCreate.Add((existing, shaped.Order));
                }
                catch (Exception ex)
                {
                    existing.Status = "Failed";
                    existing.Error = ex.Message;
                    run.Errors.Add($"Order {reference}: {ex.Message}");
                    _logger.LogError(ex, "Failed to build order {Reference}", reference);
                }
            }
        }

        rotation.CustomerIndex = customerIndex;
        rotation.OrderItemIndex = orderItemIndex;
        _runLogStore.SaveRotation(rotation);

        for (var offset = 0; offset < pendingCreate.Count; offset += 20)
        {
            var chunk = pendingCreate.Skip(offset).Take(20).ToList();
            await CreateChunkAsync(chunk, catalog.Location.LocationName ?? "default", run, cancellationToken)
                .ConfigureAwait(false);
            _runLogStore.Save(run);
        }

        await ProcessPendingAsync(run, catalog.Location.StockLocationId, cancellationToken).ConfigureAwait(false);
        _runLogStore.Save(run);
    }

    private async Task CreateChunkAsync(
        List<(OrderRunItem Item, ChannelOrder Payload)> chunk,
        string locationName,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        List<Guid> createdIds;
        try
        {
            createdIds = await _client.CreateOrdersAsync(
                chunk.Select(x => x.Payload).ToList(),
                locationName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var (item, _) in chunk)
            {
                item.Status = "Failed";
                item.Error = ex.Message;
                run.Errors.Add($"Order {item.Reference}: {ex.Message}");
            }

            _logger.LogError(ex, "CreateOrders failed for {Count} orders", chunk.Count);
            return;
        }

        if (createdIds.Count != chunk.Count)
        {
            var message =
                $"CreateOrders returned {createdIds.Count} ids for {chunk.Count} payloads.";
            foreach (var (item, _) in chunk)
            {
                item.Status = "Failed";
                item.Error = message;
                run.Errors.Add($"Order {item.Reference}: {message}");
            }

            return;
        }

        for (var i = 0; i < chunk.Count; i++)
        {
            var item = chunk[i].Item;
            item.OrderId = createdIds[i];
            if (item.ClearSubSource)
            {
                try
                {
                    await _client.SetOrderSubSourceAsync(item.OrderId.Value, "", cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation("Cleared subsource on {Reference}", item.Reference);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Linnworks did not accept an empty subsource on {Reference}", item.Reference);
                }
            }

            item.Status = item.ShouldProcess ? "Created" : "Open";
            item.Error = null;
        }
    }

    private async Task ProcessPendingAsync(
        DailyRunRecord run,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var pending = run.Orders
            .Where(o => o.ShouldProcess && !o.Processed && o.OrderId.HasValue && !string.IsNullOrWhiteSpace(o.ScenarioId))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var ids = pending.Select(o => o.OrderId!.Value).ToList();
        _logger.LogInformation("Printing invoices/labels for {Count} orders before process", ids.Count);
        await EnsureInvoicesAndLabelsAsync(ids, cancellationToken).ConfigureAwait(false);

        var single = pending.FirstOrDefault(o => o.ScenarioId == "ORD-BASE-PAID") ?? pending[0];
        await ProcessOneAsync(single, locationId, cancellationToken).ConfigureAwait(false);

        var rest = pending.Where(o => o.OrderId != single.OrderId && !o.Processed).ToList();
        if (rest.Count == 0)
        {
            return;
        }

        try
        {
            var results = await _client.ProcessOrdersInBatchAsync(
                rest.Select(o => o.OrderId!.Value).ToList(),
                locationId,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ORD-BATCH-PROCESS processed a batch of {Count}", rest.Count);
            ApplyBatchResults(rest, results ?? []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProcessOrdersInBatch failed. Processing the remaining orders one by one.");
            foreach (var order in rest.Where(o => !o.Processed))
            {
                await ProcessOneAsync(order, locationId, cancellationToken).ConfigureAwait(false);
            }
        }

        // Retry anything still unprocessed, including batch invoice failures previously skipped.
        foreach (var order in rest.Where(o => !o.Processed))
        {
            await ProcessOneAsync(order, locationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureInvoicesAndLabelsAsync(
        IReadOnlyList<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return;
        }

        try
        {
            var templateRowId = await ResolveInvoiceTemplateRowIdAsync(cancellationToken).ConfigureAwait(false);
            if (templateRowId is > 0)
            {
                foreach (var chunk in orderIds.Chunk(10))
                {
                    await _client.CreateInvoicePdfAsync(templateRowId.Value, chunk.ToList(), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreatePDFfromJobForceTemplate for Invoice failed; falling back to SetInvoicesPrinted.");
        }

        foreach (var chunk in orderIds.Chunk(10))
        {
            var ids = chunk.ToList();
            await _client.SetInvoicesPrintedAsync(ids, cancellationToken).ConfigureAwait(false);
            await _client.SetLabelsPrintedAsync(ids, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> ResolveInvoiceTemplateRowIdAsync(CancellationToken cancellationToken)
    {
        if (_invoiceTemplateResolved)
        {
            return _invoiceTemplateRowId;
        }

        _invoiceTemplateResolved = true;
        var templates = await _client.GetInvoiceTemplatesAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var template = templates.FirstOrDefault(t => t.Enabled && t.IsDefault)
            ?? templates.FirstOrDefault(t => t.Enabled)
            ?? templates.FirstOrDefault();
        if (template is null || template.pkTemplateRowId <= 0)
        {
            _logger.LogWarning("No Invoice print template found; falling back to SetInvoicesPrinted only.");
            return null;
        }

        _invoiceTemplateRowId = template.pkTemplateRowId;
        _logger.LogInformation(
            "Using Invoice template {TemplateId} ({TemplateName})",
            template.pkTemplateRowId,
            template.TemplateName);
        return _invoiceTemplateRowId;
    }

    private static void ApplyBatchResults(IReadOnlyList<OrderRunItem> orders, IReadOnlyList<ProcessOrderResult> results)
    {
        foreach (var order in orders)
        {
            var result = results.FirstOrDefault(r => r.OrderId == order.OrderId);
            if (result is null)
            {
                continue;
            }

            order.Processed = result.Processed;
            order.Status = result.Processed ? "Processed" : "ProcessFailed";
            order.Error = result.Processed ? null : result.Error;
        }
    }

    private async Task ProcessOneAsync(OrderRunItem item, Guid locationId, CancellationToken cancellationToken)
    {
        if (!item.OrderId.HasValue || item.Processed)
        {
            return;
        }

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var orderIds = new List<Guid> { item.OrderId.Value };
                await EnsureInvoicesAndLabelsAsync(orderIds, cancellationToken).ConfigureAwait(false);
                if (attempt > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
                }

                var result = await _client.ProcessOrderAsync(item.OrderId.Value, locationId, cancellationToken)
                    .ConfigureAwait(false);
                item.Processed = result.Processed;
                item.Status = result.Processed ? "Processed" : "ProcessFailed";
                item.Error = result.Processed ? null : result.Error;
                if (result.Processed)
                {
                    return;
                }

                if (!NeedsInvoiceRetry(result.Error) || attempt == maxAttempts)
                {
                    _logger.LogError("ProcessOrder failed for {Reference}: {Error}", item.Reference, result.Error);
                    return;
                }

                _logger.LogWarning(
                    "ProcessOrder for {Reference} needs invoice retry ({Attempt}/{Max}): {Error}",
                    item.Reference,
                    attempt,
                    maxAttempts,
                    result.Error);
            }
            catch (Exception ex)
            {
                item.Processed = false;
                item.Status = "ProcessFailed";
                item.Error = ex.Message;
                if (attempt == maxAttempts || !NeedsInvoiceRetry(ex.Message))
                {
                    _logger.LogError(ex, "ProcessOrder failed for {Reference}", item.Reference);
                    return;
                }

                _logger.LogWarning(
                    ex,
                    "ProcessOrder for {Reference} needs invoice retry ({Attempt}/{Max})",
                    item.Reference,
                    attempt,
                    maxAttempts);
            }
        }
    }

    private static bool NeedsInvoiceRetry(string? error)
        => !string.IsNullOrWhiteSpace(error)
           && error.Contains("invoice", StringComparison.OrdinalIgnoreCase);

    private ShapedOrder Shape(
        ResolvedCatalog catalog,
        StockPool pool,
        OrderScenarioSpec spec,
        int index,
        string reference,
        SeedCustomer customer,
        ref int compositeIndex,
        ref int orderItemIndex)
    {
        var payment = catalog.PickPayment(orderItemIndex + index);
        var postal = catalog.PickPostal(orderItemIndex + index);
        var currency = spec.ScenarioId == "ORD-CURRENCY-NONBASE"
            ? catalog.NonBaseCurrency(index) ?? throw new InvalidOperationException(
                "ORD-CURRENCY-NONBASE needs a non-base currency in Linnworks (a conversion rate other than 1).")
            : catalog.BaseCurrency();
        var subSource = catalog.PickSubSource(orderItemIndex + index);
        var billing = Clone(customer.Address);
        var delivery = Clone(customer.Address);
        var useChannelTax = true;
        var taxInclusive = true;
        var zeroTax = false;
        var itemTaxes = false;
        double? headerDiscount = null;
        DiscountTaxType? discountTax = null;
        double postage = 0;
        double? postageDiscount = null;
        double? postageTax = null;
        var hold = false;
        var unpaid = spec.ScenarioId is "ORD-UNPAID" or "ORD-NO-PROCESS";
        var lines = new List<ChannelOrderItem>();
        var linkBySku = true;

        switch (spec.ScenarioId)
        {
            case "ORD-TAX-EXCL":
                taxInclusive = false;
                if (index == 1)
                {
                    useChannelTax = false;
                }
                else if (index == 2)
                {
                    itemTaxes = true;
                }
                else if (index == 3)
                {
                    zeroTax = true;
                }

                lines.Add(StockLine(pool.Take(1), 1, taxInclusive, useChannelTax, zeroTax, false, itemTaxes));
                break;
            case "ORD-SHIP-COST":
                postage = ShippingCost;
                if (index is 1 or 2)
                {
                    postageTax = 20;
                }
                else if (index is 3 or 4)
                {
                    postageDiscount = ShippingDiscount;
                }

                lines.Add(StockLine(pool.Take(1), 1, true, true, false, false, false));
                break;
            case "ORD-DISC-LINE":
                lines.Add(StockLine(pool.Take(1), 1, true, true, false, true, false));
                break;
            case "ORD-DISC-ORDER":
                postage = ShippingCost;
                headerDiscount = 2;
                discountTax = index == 2 ? DiscountTaxType.DeductAfterTax : DiscountTaxType.DeductBeforeTax;
                lines.Add(StockLine(pool.Take(1), 1, true, true, false, false, false));
                break;
            case "ORD-SERVICE-LINE":
                linkBySku = false;
                lines.Add(new ChannelOrderItem
                {
                    IsService = true,
                    ItemNumber = "SEED-SERVICE",
                    ChannelSKU = "SEED-SERVICE",
                    ItemTitle = "Seed service line",
                    Qty = 1,
                    PricePerUnit = 15,
                    TaxRate = 20,
                    TaxCostInclusive = true,
                    UseChannelTax = true,
                    LineDiscount = 0
                });
                break;
            case "ORD-QTY-GT1":
                lines.Add(StockLine(pool.Take(3), 3, true, true, false, false, false));
                break;
            case "ORD-MULTI-LINE":
                var pair = pool.TakePair();
                lines.Add(StockLine(pair.First, 1, true, true, false, false, false));
                lines.Add(StockLine(pair.Second, 1, true, true, false, false, false));
                break;
            case "ORD-COMPOSITE":
                if (catalog.CompositeItems.Count == 0)
                {
                    throw new InvalidOperationException(
                        "ORD-COMPOSITE needs a composite SKU at this location. None were found.");
                }

                var composite = catalog.CompositeItems[compositeIndex % catalog.CompositeItems.Count];
                compositeIndex++;
                lines.Add(StockLine(composite, 1, true, true, false, false, false));
                break;
            case "ORD-STATE-HOLD":
                hold = true;
                lines.Add(StockLine(pool.Take(1), 1, true, true, false, false, false));
                break;
            case "ORD-ADV-TAX-RULE":
                var taxCountry = TaxCountries[(index - 1) % TaxCountries.Length];
                delivery = WithCountry(delivery, taxCountry);
                if (index == 1)
                {
                    if (string.Equals(billing.Country, delivery.Country, StringComparison.OrdinalIgnoreCase))
                    {
                        billing = WithCountry(billing, TaxCountries[1]);
                    }
                }
                else
                {
                    billing = Clone(delivery);
                }

                lines.Add(StockLine(pool.Take(1), 1, true, true, false, false, false));
                break;
            default:
                lines.Add(StockLine(pool.Take(1), 1, true, true, false, false, false));
                break;
        }

        if (headerDiscount is > 0)
        {
            var goods = lines.Sum(l => l.PricePerUnit * l.Qty);
            if (headerDiscount.Value >= goods + postage)
            {
                headerDiscount = Math.Round((goods + postage) / 2, 2, MidpointRounding.AwayFromZero);
            }
        }

        var refundablePostage = postageDiscount is > 0
            ? Math.Round(postage - postageDiscount.Value, 2, MidpointRounding.AwayFromZero)
            : postage;
        var now = DateTime.UtcNow;
        var order = new ChannelOrder
        {
            Source = _options.Source,
            SubSource = subSource,
            ReferenceNumber = reference,
            ReceivedDate = now,
            DispatchBy = now.AddDays(7),
            PaidOn = unpaid ? null : now,
            PaymentStatus = unpaid ? ChannelPaymentStatus.Unpaid : ChannelPaymentStatus.Paid,
            OrderStatusType = ChannelOrderStatus.Unshipped,
            OrderStatus = "Unshipped",
            OrderState = hold ? ChannelOrderState.Hold : ChannelOrderState.None,
            Currency = currency.Currency,
            ConversionRate = currency.ConversionRate > 0 ? currency.ConversionRate : 1,
            ChannelBuyerName = customer.FullName,
            MatchPaymentMethodTag = payment.Name,
            PaymentMethodName = payment.Name,
            SavePaymentMethodIfNotExist = false,
            MatchPostalServiceTag = postal.PostalServiceName,
            PostalServiceName = postal.PostalServiceName,
            SavePostalServiceIfNotExist = false,
            AutomaticallyLinkBySKU = linkBySku,
            AutomaticallyLinkByBarcode = linkBySku,
            AutomaticallyLinkByASIN = linkBySku,
            UseChannelTax = useChannelTax,
            BillingAddress = billing,
            DeliveryAddress = delivery,
            PostalServiceCost = postage > 0 ? postage : null,
            PostalServiceTaxRate = postageTax,
            PostalServiceDiscount = postageDiscount,
            Discount = headerDiscount,
            DiscountType = headerDiscount is > 0 ? DiscountType.AllEvenly : null,
            DiscountTaxType = headerDiscount is > 0 ? discountTax : null,
            Notes =
            [
                new ChannelOrderNote
                {
                    Note = $"Created by LinnworksDailySeed. Scenario {spec.ScenarioId}.",
                    NoteUserName = "LinnworksDailySeed"
                }
            ],
            OrderItems = lines
        };

        return new ShapedOrder(
            order,
            lines[0].PricePerUnit * lines[0].Qty,
            Math.Max(1, lines[0].Qty),
            refundablePostage);
    }

    private static ChannelOrderItem StockLine(
        StockItem item,
        int qty,
        bool taxInclusive,
        bool useChannelTax,
        bool zeroTax,
        bool lineDiscount,
        bool itemTaxes)
    {
        var price = UnitPrice(item);
        var taxRate = zeroTax ? 0 : item.TaxRate > 0 ? item.TaxRate : 20;
        double discount = 0;
        if (lineDiscount)
        {
            discount = Math.Round(Math.Min(1.5, price * 0.15), 2, MidpointRounding.AwayFromZero);
            if (discount <= 0 || discount >= price)
            {
                discount = Math.Round(price / 2, 2, MidpointRounding.AwayFromZero);
            }
        }

        var line = new ChannelOrderItem
        {
            TaxCostInclusive = taxInclusive,
            UseChannelTax = useChannelTax,
            PricePerUnit = price,
            Qty = qty,
            TaxRate = taxRate,
            LineDiscount = discount,
            ItemNumber = item.ItemNumber,
            ChannelSKU = item.ItemNumber,
            IsService = false,
            ItemTitle = item.ItemTitle ?? item.ItemNumber
        };
        if (itemTaxes)
        {
            line.Taxes =
            [
                new ChannelOrderItemTax
                {
                    TaxType = "VAT",
                    TaxValue = Math.Round(price * taxRate / 100d, 2, MidpointRounding.AwayFromZero),
                    IsSellerCollected = true
                }
            ];
        }

        return line;
    }

    private static string AlsoCovers(string scenarioId, int index) => scenarioId switch
    {
        "ORD-BASE-PAID" => "ORD-TAX-INCL, ORD-PAID, ORD-STATUS-UNSHIPPED, ORD-STATE-NONE, ORD-CURRENCY-BASE, ORD-SOURCE-SUBSOURCE, ORD-PROCESS, ORD-INVOICE-LABEL, ORD-EMPTY-SUBSOURCE",
        "ORD-TAX-EXCL" when index == 1 => "ORD-USE-CHANNEL-TAX-OFF",
        "ORD-TAX-EXCL" when index == 2 => "ORD-CHANNEL-ITEM-TAXES",
        "ORD-TAX-EXCL" when index == 3 => "ORD-ZERO-TAX",
        "ORD-SHIP-COST" when index is 1 or 2 => "ORD-SHIP-TAX-RATE",
        "ORD-SHIP-COST" when index is 3 or 4 => "ORD-SHIP-DISCOUNT",
        "ORD-DISC-ORDER" when index == 2 => "ORD-DISC-ALL-EVENLY, ORD-DISC-TAX-AFTER, ORD-SHIP-COST",
        "ORD-DISC-ORDER" => "ORD-DISC-ALL-EVENLY, ORD-DISC-TAX-BEFORE, ORD-SHIP-COST",
        "ORD-ADV-TAX-RULE" when index == 1 => "ORD-DIFF-BILL-SHIP",
        _ => ""
    };

    private static bool IsCreated(OrderRunItem item)
        => item.OrderId.HasValue && (item.Processed || !item.ShouldProcess || item.Status is "Open" or "Cancelled");

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

    private static double UnitPrice(StockItem item)
    {
        var price = item.RetailPrice is > 0
            ? item.RetailPrice.Value
            : item.PurchasePrice > 0 ? item.PurchasePrice : 10;
        return Math.Round(price, 2, MidpointRounding.AwayFromZero);
    }

    private static ChannelAddress Clone(ChannelAddress source) => new()
    {
        FullName = source.FullName,
        Company = source.Company,
        Address1 = source.Address1,
        Address2 = source.Address2,
        Address3 = source.Address3,
        Town = source.Town,
        Region = source.Region,
        PostCode = source.PostCode,
        Country = source.Country,
        CountryCode = source.CountryCode,
        PhoneNumber = source.PhoneNumber,
        EmailAddress = source.EmailAddress
    };

    private static ChannelAddress WithCountry(
        ChannelAddress source,
        (string Country, string Code, string Region, string Town, string PostCode) country)
    {
        var copy = Clone(source);
        copy.Country = country.Country;
        copy.CountryCode = country.Code;
        copy.Region = country.Region;
        copy.Town = country.Town;
        copy.PostCode = country.PostCode;
        copy.Address1 = "1 Seed Street";
        return copy;
    }

    private sealed class StockPool
    {
        private readonly List<StockItem> _items;
        private readonly Dictionary<Guid, int> _remaining;
        private int _index;

        public StockPool(IReadOnlyList<StockItem> items, int startIndex)
        {
            _items = items.ToList();
            _remaining = items.ToDictionary(i => i.StockItemId, i => Math.Max(0, i.Available));
            _index = startIndex;
        }

        public StockItem Take(int qty)
        {
            if (_items.Count == 0)
            {
                throw new InvalidOperationException("No stock item is available.");
            }

            for (var attempt = 0; attempt < _items.Count; attempt++)
            {
                var item = _items[_index % _items.Count];
                _index++;
                if (_remaining.GetValueOrDefault(item.StockItemId) < qty)
                {
                    continue;
                }

                _remaining[item.StockItemId] -= qty;
                return item;
            }

            throw new InvalidOperationException(
                $"No stock item has {qty} available after the purchase-order deliveries.");
        }

        public (StockItem First, StockItem Second) TakePair()
        {
            var first = Take(1);
            for (var attempt = 0; attempt < _items.Count; attempt++)
            {
                var item = _items[_index % _items.Count];
                _index++;
                if (item.StockItemId == first.StockItemId)
                {
                    continue;
                }

                if (_remaining.GetValueOrDefault(item.StockItemId) < 1)
                {
                    continue;
                }

                _remaining[item.StockItemId] -= 1;
                return (first, item);
            }

            throw new InvalidOperationException("ORD-MULTI-LINE needs two different SKUs with available stock.");
        }
    }

    private sealed record ShapedOrder(ChannelOrder Order, double LineAmount, int LineQty, double Postage);
}
