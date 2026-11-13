using LinnworksDailySeed.DependencyInjection;
using LinnworksDailySeed.Worker;

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

builder.Services.AddFileRunLogStore();
builder.Services.AddLinnworksDailySeed(builder.Configuration);
builder.Services.AddHostedService<DailySeedWorker>();

var host = builder.Build();
host.Run();
