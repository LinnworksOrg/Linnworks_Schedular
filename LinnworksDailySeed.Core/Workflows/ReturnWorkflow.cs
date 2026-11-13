using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Scenarios;

namespace LinnworksDailySeed.Workflows;

public sealed class ReturnWorkflow
{
    private readonly LinnworksClient _client;
    private readonly ILogger<ReturnWorkflow> _logger;

    public ReturnWorkflow(LinnworksClient client, ILogger<ReturnWorkflow> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task EnsureReturnsAsync(
        DailyRunRecord run,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var refunded = run.Refunds
            .Where(r => r.Actioned)
            .Select(r => r.OrderReference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in DailyScenarioPlan.Returns)
        {
            var candidates = run.Orders
                .Where(o => o.ScenarioId == spec.SourceScenarioId && o.Processed && o.OrderId.HasValue)
                .Where(o => !refunded.Contains(o.Reference))
                .OrderBy(o => o.Reference)
                .Take(spec.Count)
                .ToList();
            if (candidates.Count < spec.Count)
            {
                var message =
                    $"{spec.ScenarioId} needs {spec.Count} processed {spec.SourceScenarioId} orders that were not refunded, found {candidates.Count}.";
                run.Errors.Add(message);
                _logger.LogError("{Message}", message);
                continue;
            }

            foreach (var order in candidates)
            {
                await ReturnOneAsync(spec, order, run, locationId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReturnOneAsync(
        ReturnScenarioSpec spec,
        OrderRunItem order,
        DailyRunRecord run,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var existing = run.Returns.FirstOrDefault(r =>
            r.ScenarioId == spec.ScenarioId && r.OrderReference == order.Reference);
        if (existing is not null && existing.Booked)
        {
            _logger.LogInformation("Skipping booked {Scenario} for {Reference}", spec.ScenarioId, order.Reference);
            return;
        }

        existing ??= new ReturnRunItem
        {
            ScenarioId = spec.ScenarioId,
            OrderReference = order.Reference,
            OrderId = order.OrderId
        };
        existing.ScenarioId = spec.ScenarioId;
        existing.OrderId = order.OrderId;
        if (!run.Returns.Contains(existing))
        {
            run.Returns.Add(existing);
        }

        try
        {
            var details = await _client.GetOrderByIdAsync(order.OrderId!.Value, cancellationToken).ConfigureAwait(false);
            var line = details.Items.FirstOrDefault(i => !i.IsService)
                ?? throw new InvalidOperationException($"Order {order.Reference} has no returnable line.");
            var qty = Math.Max(1, Math.Min(line.Quantity, 1));
            _logger.LogInformation(
                "Creating {Scenario} for {Reference} row {RowId} qty {Qty}",
                spec.ScenarioId,
                order.Reference,
                line.RowId,
                qty);
            await _client.CreateReturnAsync(
                order.OrderId.Value,
                [
                    new ReturnRowQty
                    {
                        OrderItemRowId = line.RowId,
                        Qty = qty,
                        Refund = spec.WithRefund ? qty : 0,
                        ReturnItemSKU = line.SKU ?? line.ChannelSKU,
                        ReturnItemTitle = line.Title
                    }
                ],
                locationId,
                spec.ScenarioId,
                spec.WithRefund,
                cancellationToken).ConfigureAwait(false);

            if (spec.WithRefund)
            {
                await EnsureReturnRefundAsync(order, line, run, cancellationToken).ConfigureAwait(false);
            }

            existing.Booked = true;
            existing.Status = "Booked";
            existing.Error = null;
        }
        catch (Exception ex) when (AlreadyReturned(ex))
        {
            existing.Booked = true;
            existing.Status = "Booked";
            existing.Error = null;
            _logger.LogInformation(
                "{Scenario} for {Reference} is already returned in Linnworks: {Message}",
                spec.ScenarioId,
                order.Reference,
                ex.Message);
        }
        catch (Exception ex)
        {
            existing.Status = "Failed";
            existing.Error = ex.Message;
            run.Errors.Add($"Return {spec.ScenarioId} {order.Reference}: {ex.Message}");
            _logger.LogError(ex, "Failed {Scenario} for {Reference}", spec.ScenarioId, order.Reference);
        }
    }

    private static bool AlreadyReturned(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("exceed original order quantity", StringComparison.OrdinalIgnoreCase)
               || message.Contains("already been returned", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no returnable", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureReturnRefundAsync(
        OrderRunItem order,
        OrderItem line,
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var existing = run.Refunds.FirstOrDefault(r =>
            r.ScenarioId == "RET-RETURN-REFUND" && r.OrderReference == order.Reference);
        if (existing is not null && existing.Actioned)
        {
            return;
        }

        existing ??= new RefundRunItem
        {
            ScenarioId = "RET-RETURN-REFUND",
            OrderReference = order.Reference,
            OrderId = order.OrderId
        };
        if (!run.Refunds.Contains(existing))
        {
            run.Refunds.Add(existing);
        }

        try
        {
            if (!existing.RefundHeaderId.HasValue)
            {
                var amount = line.CostIncTax > 0
                    ? line.CostIncTax
                    : line.Cost > 0
                        ? line.Cost
                        : line.PricePerUnit * Math.Max(1, line.Quantity);
                var created = await _client.CreateRefundAsync(
                    new CreateRefundRequest
                    {
                        ChannelInitiated = false,
                        OrderId = order.OrderId!.Value,
                        RefundLines =
                        [
                            new NewRefundLine
                            {
                                OrderItemRowId = line.RowId,
                                RefundedUnit = "Item",
                                Amount = amount,
                                Quantity = Math.Max(1, line.Quantity),
                                FreeTextOrNote = "AUTO-SEED RET-RETURN-REFUND"
                            }
                        ]
                    },
                    cancellationToken).ConfigureAwait(false);
                if (created.RefundHeaderId <= 0)
                {
                    throw new InvalidOperationException(
                        $"Return refund was not created. {created.CannotRefundReason} {string.Join("; ", created.Errors)}");
                }

                existing.RefundHeaderId = created.RefundHeaderId;
            }

            var action = await _client.ActionRefundAsync(
                new ActionRefundRequest
                {
                    RefundHeaderId = existing.RefundHeaderId!.Value,
                    OrderId = order.OrderId!.Value
                },
                cancellationToken).ConfigureAwait(false);
            if (!action.SuccessfullyActioned)
            {
                throw new InvalidOperationException(action.CannotRefundReason ?? string.Join("; ", action.Errors));
            }

            existing.Actioned = true;
            existing.Status = "Actioned";
            existing.Error = null;
        }
        catch (Exception ex) when (AlreadyRefunded(ex))
        {
            existing.Actioned = true;
            existing.Status = "Actioned";
            existing.Error = null;
            _logger.LogInformation(
                "RET-RETURN-REFUND for {Reference} is already covered by the return: {Message}",
                order.Reference,
                ex.Message);
        }
    }

    private static bool AlreadyRefunded(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("already refund", StringComparison.OrdinalIgnoreCase)
               || message.Contains("over-refund", StringComparison.OrdinalIgnoreCase)
               || message.Contains("exceed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("cannot refund", StringComparison.OrdinalIgnoreCase);
    }
}
