using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using LinnworksDailySeed.RunLog;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Workflows;

public sealed class RefundWorkflow
{
    private readonly LinnworksClient _client;
    private readonly SeedOptions _options;
    private readonly ILogger<RefundWorkflow> _logger;

    public RefundWorkflow(
        LinnworksClient client,
        IOptions<SeedOptions> options,
        ILogger<RefundWorkflow> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureRefundsAsync(
        DailyRunRecord run,
        CancellationToken cancellationToken)
    {
        var processedOrders = run.Orders
            .Where(o => o.Processed && o.OrderId.HasValue)
            .OrderBy(o => o.Reference)
            .Take(_options.RefundCount)
            .ToList();

        if (processedOrders.Count < _options.RefundCount)
        {
            throw new InvalidOperationException(
                $"Need {_options.RefundCount} processed orders for refunds, found {processedOrders.Count}.");
        }

        foreach (var order in processedOrders)
        {
            var existing = run.Refunds.FirstOrDefault(r => r.OrderReference == order.Reference);
            if (existing is not null && existing.Actioned)
            {
                _logger.LogInformation("Skipping existing actioned refund for {Reference}", order.Reference);
                continue;
            }

            existing ??= new RefundRunItem
            {
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
                    var details = await _client.GetOrderByIdAsync(order.OrderId!.Value, cancellationToken)
                        .ConfigureAwait(false);
                    var line = details.Items.FirstOrDefault(i => !i.IsService)
                        ?? throw new InvalidOperationException(
                            $"Order {order.Reference} has no refundable item line.");

                    var amount = line.CostIncTax > 0
                        ? line.CostIncTax
                        : line.Cost > 0
                            ? line.Cost
                            : line.PricePerUnit * Math.Max(1, line.Quantity);

                    _logger.LogInformation(
                        "Creating refund for {Reference} amount {Amount} row {RowId}",
                        order.Reference,
                        amount,
                        line.RowId);

                    var createResponse = await _client.CreateRefundAsync(
                        new CreateRefundRequest
                        {
                            ChannelInitiated = false,
                            OrderId = order.OrderId.Value,
                            RefundLines =
                            [
                                new NewRefundLine
                                {
                                    OrderItemRowId = line.RowId,
                                    RefundedUnit = "Item",
                                    Amount = amount,
                                    Quantity = 1,
                                    FreeTextOrNote = "AUTO-SEED"
                                }
                            ]
                        },
                        cancellationToken).ConfigureAwait(false);

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

                    existing.RefundHeaderId = createResponse.RefundHeaderId;
                    existing.Status = "Created";
                }

                _logger.LogInformation(
                    "Actioning refund header {HeaderId} for {Reference}",
                    existing.RefundHeaderId,
                    order.Reference);

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
                    run.Errors.Add($"Refund {order.Reference}: {detail}");
                    _logger.LogWarning(
                        "ActionRefund did not succeed for {Reference}: {Detail}. Leaving refund unactioned.",
                        order.Reference,
                        detail);
                    continue;
                }

                existing.Actioned = true;
                existing.Status = "Actioned";
                existing.Error = null;
            }
            catch (Exception ex)
            {
                existing.Status = "Failed";
                existing.Error = ex.Message;
                run.Errors.Add($"Refund {order.Reference}: {ex.Message}");
                _logger.LogError(ex, "Failed refund for {Reference}", order.Reference);
                throw;
            }
        }
    }
}
