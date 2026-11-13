using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.RunLog;

namespace LinnworksDailySeed.Workflows;

public sealed class CancelWorkflow
{
    private readonly LinnworksClient _client;
    private readonly ILogger<CancelWorkflow> _logger;

    public CancelWorkflow(LinnworksClient client, ILogger<CancelWorkflow> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task EnsureCancelsAsync(
        DailyRunRecord run,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var targets = run.Orders
            .Where(o => o.ScenarioId is "CNL-OPEN-ORDER" or "CNL-WITH-REFUND")
            .ToList();
        foreach (var order in targets)
        {
            if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping cancelled {Reference}", order.Reference);
                continue;
            }

            if (!order.OrderId.HasValue)
            {
                order.Status = "Failed";
                order.Error = "Cancel target was not created.";
                run.Errors.Add($"Cancel {order.Reference}: {order.Error}");
                continue;
            }

            var refund = order.ScenarioId == "CNL-WITH-REFUND"
                ? order.LineAmount > 0 ? Math.Min(1d, order.LineAmount) : 1d
                : 0d;
            try
            {
                _logger.LogInformation(
                    "Cancelling {Scenario} {Reference} refund={Refund}",
                    order.ScenarioId,
                    order.Reference,
                    refund);
                await _client.CancelOrderAsync(
                    order.OrderId.Value,
                    locationId,
                    refund,
                    $"LinnworksDailySeed {order.ScenarioId}",
                    cancellationToken).ConfigureAwait(false);
                order.Status = "Cancelled";
                order.Processed = false;
                order.Error = null;
            }
            catch (Exception ex)
            {
                order.Status = "Failed";
                order.Error = ex.Message;
                run.Errors.Add($"Cancel {order.Reference}: {ex.Message}");
                _logger.LogError(ex, "Failed to cancel {Reference}", order.Reference);
            }
        }
    }
}
