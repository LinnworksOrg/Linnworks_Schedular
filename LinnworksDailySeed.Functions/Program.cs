using LinnworksDailySeed.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var storageConnection = context.Configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException(
                "AzureWebJobsStorage is required so the daily seed can persist run logs in blob storage.");

        services.AddBlobRunLogStore(storageConnection);
        services.AddLinnworksDailySeed(context.Configuration);
    })
    .Build();

host.Run();
