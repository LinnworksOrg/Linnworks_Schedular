using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Workflows;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Worker;

public sealed class DailySeedWorker : BackgroundService
{
    private readonly DailyRun _dailyRun;
    private readonly SeedOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DailySeedWorker> _logger;
    private readonly bool _runOnce;
    private readonly bool _force;

    public DailySeedWorker(
        DailyRun dailyRun,
        IOptions<SeedOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<DailySeedWorker> logger,
        IConfiguration configuration)
    {
        _dailyRun = dailyRun;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
        var args = Environment.GetCommandLineArgs();
        _runOnce = configuration.GetValue<bool>("RunOnce")
            || args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));
        _force = configuration.GetValue<bool>("Force")
            || args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runOnce)
        {
            _logger.LogInformation(
                "Starting one-shot seed run (--once{Force}).",
                _force ? ", --force" : string.Empty);
            try
            {
                var tz = ResolveTimeZone();
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
                await _dailyRun.ExecuteAsync(
                        DateOnly.FromDateTime(nowLocal.DateTime),
                        stoppingToken,
                        force: _force)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "One-shot seed run failed.");
                Environment.ExitCode = 1;
            }
            finally
            {
                _lifetime.StopApplication();
            }

            return;
        }

        _logger.LogInformation(
            "LinnworksDailySeed service started. Schedule={RunAt} TimeZone={TimeZone}",
            _options.RunAt,
            _options.TimeZone);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextRun();
                _logger.LogInformation("Next run in {Delay}", delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var tz = ResolveTimeZone();
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
                await _dailyRun.ExecuteAsync(
                        DateOnly.FromDateTime(nowLocal.DateTime),
                        stoppingToken,
                        force: false)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled seed run failed. Will retry on the next schedule.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var tz = ResolveTimeZone();
        if (!TimeSpan.TryParse(_options.RunAt, out var runAt))
        {
            throw new InvalidOperationException(
                $"Seed:RunAt '{_options.RunAt}' is invalid. Use HH:mm, for example 09:00.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var nextLocalDateTime = nowLocal.Date + runAt;
        if (nowLocal.TimeOfDay >= runAt)
        {
            nextLocalDateTime = nextLocalDateTime.AddDays(1);
        }

        var unspecified = DateTime.SpecifyKind(nextLocalDateTime, DateTimeKind.Unspecified);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        var delay = nextUtc - nowUtc.UtcDateTime;
        return delay < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            if (string.Equals(_options.TimeZone, "India Standard Time", StringComparison.OrdinalIgnoreCase))
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }

            throw;
        }
    }
}
