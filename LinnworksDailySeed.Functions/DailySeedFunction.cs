using LinnworksDailySeed.Workflows;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LinnworksDailySeed.Functions;

public sealed class DailySeedFunction
{
    private readonly DailyRun _dailyRun;
    private readonly ILogger<DailySeedFunction> _logger;

    public DailySeedFunction(DailyRun dailyRun, ILogger<DailySeedFunction> logger)
    {
        _dailyRun = dailyRun;
        _logger = logger;
    }

    /// <summary>
    /// Default schedule 0 30 3-6 * * * is 09:00–12:00 IST (03:30–06:30 UTC).
    /// Override with the SeedSchedule app setting. A completed day is skipped.
    /// </summary>
    [Function(nameof(DailySeed))]
    public async Task DailySeed(
        [TimerTrigger("%SeedSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var today = TodayInIndia();
        _logger.LogInformation(
            "Daily seed timer fired for {Date}. IsPastDue={IsPastDue}",
            today,
            timer.IsPastDue);

        try
        {
            var result = await _dailyRun.ExecuteAsync(today, cancellationToken, force: false)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Daily seed finished for {Date} with status {Status}. Orders={OrderCount}, POs={PoCount}, Refunds={RefundCount}, Returns={ReturnCount}",
                today,
                result.Status,
                result.Orders.Count,
                result.PurchaseOrders.Count,
                result.Refunds.Count,
                result.Returns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily seed failed for {Date}. A later timer fire will resume.", today);
            throw;
        }
    }

    private static DateOnly TodayInIndia()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
