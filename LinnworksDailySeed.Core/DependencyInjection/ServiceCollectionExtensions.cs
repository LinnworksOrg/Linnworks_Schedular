using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinnworksDailySeed.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Linnworks clients, catalog, and daily seed workflows.
    /// Call <see cref="AddFileRunLogStore"/> or <see cref="AddBlobRunLogStore"/> before this,
    /// or register <see cref="IRunLogStore"/> yourself.
    /// </summary>
    public static IServiceCollection AddLinnworksDailySeed(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
        services.Configure<LinnworksOptions>(configuration.GetSection(LinnworksOptions.SectionName));

        services.AddHttpClient(nameof(AuthClient));
        services.AddHttpClient(nameof(LinnworksClient));

        services.AddSingleton<AuthClient>();
        services.AddSingleton<LinnworksClient>();
        services.AddSingleton<AccountCatalog>();
        services.AddSingleton<PurchaseOrderWorkflow>();
        services.AddSingleton<OrderWorkflow>();
        services.AddSingleton<CancelWorkflow>();
        services.AddSingleton<RefundWorkflow>();
        services.AddSingleton<ReturnWorkflow>();
        services.AddSingleton<DailyRun>();
        return services;
    }

    public static IServiceCollection AddFileRunLogStore(this IServiceCollection services)
    {
        services.AddSingleton<IRunLogStore, FileRunLogStore>();
        return services;
    }

    public static IServiceCollection AddBlobRunLogStore(
        this IServiceCollection services,
        string connectionString,
        string containerName = BlobRunLogStore.DefaultContainerName)
    {
        services.AddSingleton(_ => new Azure.Storage.Blobs.BlobServiceClient(connectionString));
        services.AddSingleton<IRunLogStore>(sp =>
            new BlobRunLogStore(
                sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>(),
                containerName,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlobRunLogStore>>()));
        return services;
    }
}
