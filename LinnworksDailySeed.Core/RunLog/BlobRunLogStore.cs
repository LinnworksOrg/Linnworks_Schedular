using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace LinnworksDailySeed.RunLog;

public sealed class BlobRunLogStore : IRunLogStore
{
    public const string DefaultContainerName = "seed-runs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobRunLogStore> _logger;
    private bool _ensured;

    public BlobRunLogStore(BlobServiceClient blobServiceClient, ILogger<BlobRunLogStore> logger)
        : this(blobServiceClient, DefaultContainerName, logger)
    {
    }

    public BlobRunLogStore(
        BlobServiceClient blobServiceClient,
        string containerName,
        ILogger<BlobRunLogStore> logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
    }

    public DailyRunRecord? Load(DateOnly date)
    {
        EnsureContainer();
        var blob = _container.GetBlobClient($"{date:yyyy-MM-dd}.json");
        if (!blob.Exists())
        {
            return null;
        }

        var download = blob.DownloadContent();
        return JsonSerializer.Deserialize<DailyRunRecord>(download.Value.Content.ToString());
    }

    public void Save(DailyRunRecord record)
    {
        EnsureContainer();
        var date = DateOnly.Parse(record.Date);
        var blob = _container.GetBlobClient($"{date:yyyy-MM-dd}.json");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        blob.Upload(BinaryData.FromString(json), overwrite: true);
        _logger.LogInformation("Run log written to blob {Blob}", blob.Name);
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
        EnsureContainer();
        var blob = _container.GetBlobClient("rotation.json");
        if (!blob.Exists())
        {
            return new RotationState();
        }

        var download = blob.DownloadContent();
        return JsonSerializer.Deserialize<RotationState>(download.Value.Content.ToString())
               ?? new RotationState();
    }

    public void SaveRotation(RotationState state)
    {
        EnsureContainer();
        var blob = _container.GetBlobClient("rotation.json");
        var json = JsonSerializer.Serialize(state, JsonOptions);
        blob.Upload(BinaryData.FromString(json), overwrite: true);
    }

    public void Archive(DateOnly date, DailyRunRecord record)
    {
        EnsureContainer();
        var stamp = DateTime.UtcNow.ToString("HHmmss");
        var blob = _container.GetBlobClient($"{date:yyyy-MM-dd}.{stamp}.bak.json");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        blob.Upload(BinaryData.FromString(json), overwrite: true);
        _logger.LogInformation("Archived previous run log to blob {Blob}", blob.Name);
    }

    private void EnsureContainer()
    {
        if (_ensured)
        {
            return;
        }

        _container.CreateIfNotExists(PublicAccessType.None);
        _ensured = true;
    }
}
