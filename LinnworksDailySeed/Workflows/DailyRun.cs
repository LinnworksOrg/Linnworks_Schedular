using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.RunLog;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Workflows;

public sealed class DailyRun
{
    private readonly AccountCatalog _catalog;
    private readonly PurchaseOrderWorkflow _purchaseOrders;
    private readonly OrderWorkflow _orders;
    private readonly RefundWorkflow _refunds;
    private readonly RunLogStore _runLogStore;
    private readonly SeedOptions _options;
    private readonly ILogger<DailyRun> _logger;

    public DailyRun(
        AccountCatalog catalog,
        PurchaseOrderWorkflow purchaseOrders,
        OrderWorkflow orders,
        RefundWorkflow refunds,
        RunLogStore runLogStore,
        IOptions<SeedOptions> options,
        ILogger<DailyRun> logger)
    {
        _catalog = catalog;
        _purchaseOrders = purchaseOrders;
        _orders = orders;
        _refunds = refunds;
        _runLogStore = runLogStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DailyRunRecord> ExecuteAsync(
        DateOnly runDate,
        CancellationToken cancellationToken,
        bool force = false)
    {
        ValidateCounts();

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

            await _refunds.EnsureRefundsAsync(run, cancellationToken).ConfigureAwait(false);

            run.Status = "Completed";
            run.CompletedAt = DateTimeOffset.UtcNow;
            _runLogStore.Save(run);
            _logger.LogInformation(
                "Daily seed completed for {Date}. Prefix={Prefix} POs={PoCount}, Orders={OrderCount}, Refunds={RefundCount}",
                runDate,
                run.ReferencePrefix,
                run.PurchaseOrders.Count,
                run.Orders.Count,
                run.Refunds.Count);
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

    private void ValidateCounts()
    {
        if (_options.PurchaseOrderCount < 1)
        {
            throw new InvalidOperationException("Seed:PurchaseOrderCount must be at least 1.");
        }

        if (_options.OrderCount < 1)
        {
            throw new InvalidOperationException("Seed:OrderCount must be at least 1.");
        }

        if (_options.RefundCount < 0)
        {
            throw new InvalidOperationException("Seed:RefundCount cannot be negative.");
        }

        if (_options.RefundCount > _options.OrderCount)
        {
            throw new InvalidOperationException(
                $"Seed:RefundCount ({_options.RefundCount}) cannot exceed Seed:OrderCount ({_options.OrderCount}).");
        }
    }
}
