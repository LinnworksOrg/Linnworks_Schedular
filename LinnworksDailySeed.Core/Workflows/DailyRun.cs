using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Scenarios;

namespace LinnworksDailySeed.Workflows;

public sealed class DailyRun
{
    private readonly AccountCatalog _catalog;
    private readonly PurchaseOrderWorkflow _purchaseOrders;
    private readonly OrderWorkflow _orders;
    private readonly CancelWorkflow _cancels;
    private readonly RefundWorkflow _refunds;
    private readonly ReturnWorkflow _returns;
    private readonly IRunLogStore _runLogStore;
    private readonly ILogger<DailyRun> _logger;

    public DailyRun(
        AccountCatalog catalog,
        PurchaseOrderWorkflow purchaseOrders,
        OrderWorkflow orders,
        CancelWorkflow cancels,
        RefundWorkflow refunds,
        ReturnWorkflow returns,
        IRunLogStore runLogStore,
        ILogger<DailyRun> logger)
    {
        _catalog = catalog;
        _purchaseOrders = purchaseOrders;
        _orders = orders;
        _cancels = cancels;
        _refunds = refunds;
        _returns = returns;
        _runLogStore = runLogStore;
        _logger = logger;
    }

    public async Task<DailyRunRecord> ExecuteAsync(
        DateOnly runDate,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var existing = _runLogStore.Load(runDate);
        if (!force && _runLogStore.IsDayComplete(existing))
        {
            _logger.LogInformation(
                "Run for {Date} is already completed. Skipping. Use --force to seed again the same day.",
                runDate);
            return existing!;
        }

        DailyRunRecord run;
        if (force)
        {
            if (existing is not null)
            {
                _runLogStore.Archive(runDate, existing);
            }

            var tag = DateTime.Now.ToString("HHmmss");
            run = new DailyRunRecord
            {
                Date = runDate.ToString("yyyy-MM-dd"),
                ReferencePrefix = $"AUTO-{runDate:yyyyMMdd}-F{tag}",
                Forced = true,
                StartedAt = DateTimeOffset.UtcNow,
                Status = "InProgress"
            };
            _logger.LogInformation(
                "Forced re-run for {Date}. New reference prefix={Prefix}",
                runDate,
                run.ReferencePrefix);
        }
        else
        {
            run = existing ?? new DailyRunRecord
            {
                Date = runDate.ToString("yyyy-MM-dd"),
                ReferencePrefix = $"AUTO-{runDate:yyyyMMdd}",
                StartedAt = DateTimeOffset.UtcNow,
                Status = "InProgress"
            };

            if (string.IsNullOrWhiteSpace(run.ReferencePrefix))
            {
                run.ReferencePrefix = $"AUTO-{runDate:yyyyMMdd}";
            }

            // Resume a failed run: clear terminal Failed so progress can continue.
            if (string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                run.Status = "InProgress";
                run.CompletedAt = null;
                _logger.LogInformation("Resuming failed run for {Date}.", runDate);
            }
        }

        try
        {
            var catalog = await _catalog.ResolveAsync(cancellationToken).ConfigureAwait(false);
            run.LocationName = catalog.Location.LocationName;
            run.SupplierName = $"{catalog.Suppliers.Count} existing suppliers";
            run.Sku = $"{catalog.Items.Count} existing items";
            _runLogStore.Save(run);

            await _purchaseOrders.EnsurePurchaseOrdersAsync(runDate, catalog, run, cancellationToken)
                .ConfigureAwait(false);
            _runLogStore.Save(run);

            await _orders.EnsureOrdersAsync(runDate, catalog, run, cancellationToken).ConfigureAwait(false);
            _runLogStore.Save(run);

            await _cancels.EnsureCancelsAsync(run, catalog.Location.StockLocationId, cancellationToken)
                .ConfigureAwait(false);
            _runLogStore.Save(run);

            await _refunds.EnsureRefundsAsync(run, cancellationToken).ConfigureAwait(false);
            _runLogStore.Save(run);

            await _returns.EnsureReturnsAsync(run, catalog.Location.StockLocationId, cancellationToken)
                .ConfigureAwait(false);

            var failures = DescribeFailures(run);
            if (failures.Count > 0)
            {
                foreach (var failure in failures)
                {
                    if (!run.Errors.Contains(failure))
                    {
                        run.Errors.Add(failure);
                    }
                }

                throw new InvalidOperationException(
                    $"Daily seed left {failures.Count} scenario(s) incomplete. {failures[0]}");
            }

            run.Status = "Completed";
            run.CompletedAt = DateTimeOffset.UtcNow;
            _runLogStore.Save(run);
            _logger.LogInformation(
                "Daily seed completed for {Date}. Prefix={Prefix} POs={PoCount}, Orders={OrderCount}, Refunds={RefundCount}, Returns={ReturnCount}",
                runDate,
                run.ReferencePrefix,
                run.PurchaseOrders.Count,
                run.Orders.Count,
                run.Refunds.Count,
                run.Returns.Count);
            return run;
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            run.Errors.Add(ex.Message);
            run.CompletedAt = DateTimeOffset.UtcNow;
            _runLogStore.Save(run);
            throw;
        }
    }

    private static List<string> DescribeFailures(DailyRunRecord run)
    {
        var failures = new List<string>();
        foreach (var spec in DailyScenarioPlan.PurchaseOrders)
        {
            var done = run.PurchaseOrders.Count(p =>
                p.ScenarioId == spec.ScenarioId && IsPurchaseComplete(p, spec.Partial));
            if (done < spec.Count)
            {
                failures.Add($"{spec.ScenarioId}: completed {done} of {spec.Count}.");
            }
        }

        foreach (var spec in DailyScenarioPlan.Orders)
        {
            var done = run.Orders.Count(o =>
                o.ScenarioId == spec.ScenarioId
                && (spec.Process ? o.Processed : string.Equals(o.Status, "Open", StringComparison.OrdinalIgnoreCase)));
            if (done < spec.Count)
            {
                failures.Add($"{spec.ScenarioId}: completed {done} of {spec.Count}.");
            }
        }

        foreach (var spec in DailyScenarioPlan.Cancels)
        {
            var done = run.Orders.Count(o =>
                o.ScenarioId == spec.ScenarioId
                && string.Equals(o.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));
            if (done < spec.Count)
            {
                failures.Add($"{spec.ScenarioId}: cancelled {done} of {spec.Count}.");
            }
        }

        foreach (var spec in DailyScenarioPlan.Refunds)
        {
            var done = run.Refunds.Count(r => r.ScenarioId == spec.ScenarioId && r.Actioned);
            if (done < spec.Count)
            {
                failures.Add($"{spec.ScenarioId}: actioned {done} of {spec.Count}.");
            }
        }

        foreach (var spec in DailyScenarioPlan.Returns)
        {
            var done = run.Returns.Count(r => r.ScenarioId == spec.ScenarioId && r.Booked);
            if (done < spec.Count)
            {
                failures.Add($"{spec.ScenarioId}: booked {done} of {spec.Count}.");
            }
        }

        return failures;
    }

    private static bool IsPurchaseComplete(PurchaseOrderRunItem item, bool partial)
    {
        if (!item.PurchaseId.HasValue)
        {
            return false;
        }

        if (partial)
        {
            return item.Status.Equals("Partial", StringComparison.OrdinalIgnoreCase)
                   || item.Status.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase);
        }

        return item.Status.Equals("Delivered", StringComparison.OrdinalIgnoreCase);
    }
}
