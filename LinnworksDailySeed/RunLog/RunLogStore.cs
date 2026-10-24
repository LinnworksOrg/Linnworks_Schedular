using System.Text.Json;
using LinnworksDailySeed.Configuration;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.RunLog;

public sealed class RunLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SeedOptions _options;
    private readonly ILogger<RunLogStore> _logger;

    public RunLogStore(IOptions<SeedOptions> options, ILogger<RunLogStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string GetPathForDate(DateOnly date)
    {
        var directory = Path.IsPathRooted(_options.RunLogDirectory)
            ? _options.RunLogDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.RunLogDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{date:yyyy-MM-dd}.json");
    }

    public DailyRunRecord? Load(DateOnly date)
    {
        var path = GetPathForDate(date);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DailyRunRecord>(json);
    }

    public void Save(DailyRunRecord record)
    {
        var date = DateOnly.Parse(record.Date);
        var path = GetPathForDate(date);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(path, json);
        _logger.LogInformation("Run log written to {Path}", path);
    }

    public bool IsDayComplete(DailyRunRecord? record)
    {
        if (record is null)
        {
            return false;
        }

        return string.Equals(record.Status, "Completed", StringComparison.OrdinalIgnoreCase);
    }

    public RotationState LoadRotation()
    {
        var path = RotationPath();
        if (!File.Exists(path))
        {
            return new RotationState();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RotationState>(json) ?? new RotationState();
    }

    public void SaveRotation(RotationState state)
    {
        var path = RotationPath();
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private string RotationPath()
    {
        var directory = Path.GetDirectoryName(GetPathForDate(DateOnly.FromDateTime(DateTime.UtcNow)))!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "rotation.json");
    }

    public void Archive(DateOnly date, DailyRunRecord record)
    {
        var path = GetPathForDate(date);
        var stamp = DateTime.Now.ToString("HHmmss");
        var archivePath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{date:yyyy-MM-dd}.{stamp}.bak.json");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(archivePath, json);
        _logger.LogInformation("Archived previous run log to {Path}", archivePath);
    }
}
