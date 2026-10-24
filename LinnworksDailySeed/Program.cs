using LinnworksDailySeed.Catalog;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.RunLog;
using LinnworksDailySeed.Worker;
using LinnworksDailySeed.Workflows;

var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));
var force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

if (runOnce || force)
{
    var overrides = new Dictionary<string, string?>();
    if (runOnce)
    {
        overrides["RunOnce"] = "true";
    }

    if (force)
    {
        overrides["Force"] = "true";
    }

    builder.Configuration.AddInMemoryCollection(overrides);
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LinnworksDailySeed";
});

builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<LinnworksOptions>(builder.Configuration.GetSection(LinnworksOptions.SectionName));

builder.Services.AddHttpClient(nameof(AuthClient));
builder.Services.AddHttpClient(nameof(LinnworksClient));

builder.Services.AddSingleton<AuthClient>();
builder.Services.AddSingleton<LinnworksClient>();
builder.Services.AddSingleton<AccountCatalog>();
builder.Services.AddSingleton<RunLogStore>();
builder.Services.AddSingleton<PurchaseOrderWorkflow>();
builder.Services.AddSingleton<OrderWorkflow>();
builder.Services.AddSingleton<RefundWorkflow>();
builder.Services.AddSingleton<DailyRun>();
builder.Services.AddHostedService<DailySeedWorker>();

var host = builder.Build();
host.Run();
