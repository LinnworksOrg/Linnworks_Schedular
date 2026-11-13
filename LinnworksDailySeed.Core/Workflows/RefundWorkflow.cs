using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Scenarios;

namespace LinnworksDailySeed.Workflows;

public sealed class RefundWorkflow
{
    private readonly LinnworksClient _client;
    private readonly ILogger<RefundWorkflow> _logger;

    public RefundWorkflow(LinnworksClient client, ILogger<RefundWorkflow> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task EnsureRefundsAsync(DailyRunRecord run, CancellationToken cancellationToken)
    {
        foreach (var spec in DailyScenarioPlan.Refunds)
        {
            var pool = run.Orders
                .Where(o => o.ScenarioId == spec.SourceScenarioId && o.Processed && o.OrderId.HasValue)
                .OrderBy(o => o.Reference)
                .ToList();
            if (spec.ScenarioId == "RFD-ITEM-PARTIAL-QTY")
            {
                pool = pool.Where(o => o.LineQty >= 2).ToList();
            }

            var candidates = pool.Take(spec.Count).ToList();
            if (candidates.Count < spec.Count)
            {
                var message =
                    $"{spec.ScenarioId} needs {spec.Count} processed {spec.SourceScenarioId} orders, found {candidates.Count}.";
                run.Errors.Add(message);
                _logger.LogError("{Message}", message);
                continue;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                await RefundOneAsync(spec, candidates[i], i, run, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RefundOneAsync(
        RefundScenarioSpec spec,
        OrderRunItem order,
        int index,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var existing = run.Refunds.FirstOrDefault(r =>
            r.ScenarioId == spec.ScenarioId && r.OrderReference == order.Reference);
        if (existing is not null && existing.Actioned)
        {
            _logger.LogInformation("Skipping actioned {Scenario} for {Reference}", spec.ScenarioId, order.Reference);
            return;
        }

        existing ??= new RefundRunItem
        {
            ScenarioId = spec.ScenarioId,
            OrderReference = order.Reference,
            OrderId = order.OrderId
        };
        existing.ScenarioId = spec.ScenarioId;
        existing.OrderId = order.OrderId;
        existing.AlsoCovers = index == 2 && spec.ScenarioId == "RFD-SHIPPING"
            ? "RFD-SHIP-INDEP, RFD-MANUAL, RFD-ACTION, RFD-FREETEXT-NOTE, RFD-OPTIONS-GATED"
            : "RFD-MANUAL, RFD-ACTION, RFD-FREETEXT-NOTE, RFD-OPTIONS-GATED";
        if (!run.Refunds.Contains(existing))
        {
            run.Refunds.Add(existing);
        }

        try
        {
            await ProbeOptionsAsync(order, cancellationToken).ConfigureAwait(false);
            if (!existing.RefundHeaderId.HasValue)
            {
                var lines = await BuildLinesAsync(spec, order, cancellationToken).ConfigureAwait(false);
                var createResponse = await _client.CreateRefundAsync(
                    new CreateRefundRequest
                    {
                        ChannelInitiated = false,
                        OrderId = order.OrderId!.Value,
                        RefundLines = lines
                    },
                    cancellationToken).ConfigureAwait(false);
                EnsureCreated(createResponse);
                existing.RefundHeaderId = createResponse.RefundHeaderId;
                existing.Status = "Created";
            }

            var actionResponse = await _client.ActionRefundAsync(
                new ActionRefundRequest
                {
                    RefundHeaderId = existing.RefundHeaderId!.Value,
                    OrderId = order.OrderId!.Value
                },
                cancellationToken).ConfigureAwait(false);
            if (!actionResponse.SuccessfullyActioned)
            {
                var detail = string.IsNullOrWhiteSpace(actionResponse.CannotRefundReason)
                    ? string.Join("; ", actionResponse.Errors)
                    : actionResponse.CannotRefundReason;
                existing.Status = "ActionFailed";
                existing.Error = detail;
                run.Errors.Add($"Refund {spec.ScenarioId} {order.Reference}: {detail}");
                _logger.LogWarning("ActionRefund did not succeed for {Reference}: {Detail}", order.Reference, detail);
                return;
            }

            existing.Actioned = true;
            existing.Status = "Actioned";
            existing.Error = null;
            _logger.LogInformation("Actioned {Scenario} for {Reference}", spec.ScenarioId, order.Reference);
        }
        catch (Exception ex)
        {
            existing.Status = "Failed";
            existing.Error = ex.Message;
            run.Errors.Add($"Refund {spec.ScenarioId} {order.Reference}: {ex.Message}");
            _logger.LogError(ex, "Failed {Scenario} for {Reference}", spec.ScenarioId, order.Reference);
        }
    }

    private async Task ProbeOptionsAsync(OrderRunItem order, CancellationToken cancellationToken)
    {
        try
        {
            var options = await _client.GetRefundOptionsAsync(order.OrderId!.Value, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Refund options for {Reference}: shipping={Shipping} independent={Independent} service={Service} additional={Additional} freeText={FreeText}",
                order.Reference,
                options.CanRefundShipping,
                options.CanRefundShippingIndependently,
                options.CanRefundServices,
                options.CanRefundAdditionally,
                options.CanRefundFreeText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRefundOptions failed for {Reference}. The refund will still be attempted.", order.Reference);
        }
    }

    private async Task<List<NewRefundLine>> BuildLinesAsync(
        RefundScenarioSpec spec,
        OrderRunItem order,
        CancellationToken cancellationToken)
    {
        var details = await _client.GetOrderByIdAsync(order.OrderId!.Value, cancellationToken).ConfigureAwait(false);
        var note = $"AUTO-SEED {spec.ScenarioId}";
        var itemLine = details.Items.FirstOrDefault(i => !i.IsService) ?? details.Items.FirstOrDefault();
        var serviceLine = details.Items.FirstOrDefault(i => i.IsService) ?? details.Items.FirstOrDefault();

        var seededQty = Math.Max(1, order.LineQty);
        return spec.ScenarioId switch
        {
            "RFD-ITEM-FULL" => [ItemLine(RequireItem(itemLine, order), full: true, partialAmount: false, partialQty: false, note, seededQty)],
            "RFD-ITEM-PARTIAL-AMT" => [ItemLine(RequireItem(itemLine, order), full: false, partialAmount: true, partialQty: false, note, seededQty)],
            "RFD-ITEM-PARTIAL-QTY" => [ItemLine(RequireItem(itemLine, order), full: false, partialAmount: false, partialQty: true, note, seededQty)],
            "RFD-SHIPPING" => [ShippingLine(order, note)],
            "RFD-SERVICE" => [ServiceLine(RequireItem(serviceLine, order), note)],
            "RFD-ADDITIONAL" =>
            [
                new NewRefundLine
                {
                    RefundedUnit = "Additional",
                    Amount = 5,
                    Quantity = 1,
                    FreeTextOrNote = note
                }
            ],
            "RFD-MULTI-UNIT" =>
            [
                ItemLine(RequireItem(itemLine, order), full: true, partialAmount: false, partialQty: false, note, seededQty),
                ShippingLine(order, note)
            ],
            _ => throw new InvalidOperationException($"No refund builder for {spec.ScenarioId}.")
        };
    }

    private static OrderItem RequireItem(OrderItem? line, OrderRunItem order)
        => line ?? throw new InvalidOperationException($"Order {order.Reference} has no refundable line.");

    private static NewRefundLine ItemLine(
        OrderItem line,
        bool full,
        bool partialAmount,
        bool partialQty,
        string note,
        int seededQty = 1)
    {
        var amount = LineAmount(line);
        var qty = Math.Max(Math.Max(1, line.Quantity), Math.Max(1, seededQty));
        if (partialQty)
        {
            if (qty < 2)
            {
                throw new InvalidOperationException("RFD-ITEM-PARTIAL-QTY needs a line quantity of at least 2.");
            }

            return new NewRefundLine
            {
                OrderItemRowId = line.RowId,
                RefundedUnit = "Item",
                Amount = Math.Round(amount / qty, 2, MidpointRounding.AwayFromZero),
                Quantity = 1,
                FreeTextOrNote = note
            };
        }

        if (partialAmount)
        {
            var partial = Math.Round(amount / 2, 2, MidpointRounding.AwayFromZero);
            if (partial <= 0 || partial >= amount)
            {
                partial = Math.Round(amount > 1 ? amount - 0.5 : amount / 2, 2, MidpointRounding.AwayFromZero);
            }

            return new NewRefundLine
            {
                OrderItemRowId = line.RowId,
                RefundedUnit = "Item",
                Amount = partial,
                Quantity = 1,
                FreeTextOrNote = note
            };
        }

        return new NewRefundLine
        {
            OrderItemRowId = line.RowId,
            RefundedUnit = "Item",
            Amount = amount,
            Quantity = qty,
            FreeTextOrNote = note
        };
    }

    private static NewRefundLine ShippingLine(OrderRunItem order, string note) => new()
    {
        RefundedUnit = "Shipping",
        Amount = order.Postage > 0 ? order.Postage : 4.99,
        Quantity = 1,
        FreeTextOrNote = note
    };

    private static NewRefundLine ServiceLine(OrderItem line, string note) => new()
    {
        OrderItemRowId = line.RowId,
        RefundedUnit = "Service",
        Amount = LineAmount(line),
        Quantity = Math.Max(1, line.Quantity),
        FreeTextOrNote = note
    };

    private static double LineAmount(OrderItem line)
    {
        if (line.CostIncTax > 0)
        {
            return line.CostIncTax;
        }

        if (line.Cost > 0)
        {
            return line.Cost;
        }

        return line.PricePerUnit * Math.Max(1, line.Quantity);
    }

    private static void EnsureCreated(CreateRefundResponse createResponse)
    {
        if (!string.IsNullOrWhiteSpace(createResponse.CannotRefundReason)
            && !string.Equals(createResponse.CannotRefundReason, "None", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CreateRefund blocked: {createResponse.CannotRefundReason}. {string.Join("; ", createResponse.Errors)}");
        }

        if (createResponse.RefundHeaderId <= 0)
        {
            throw new InvalidOperationException(
                $"CreateRefund did not return RefundHeaderId. Errors: {string.Join("; ", createResponse.Errors)}");
        }
    }
}
