using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SyncForge.Core.Configuration;
using SyncForge.Core.Postgres;
using SyncForge.Core.Storage;
using SyncForge.Core.Sync;
using SyncForge.Worker;

Directory.CreateDirectory(AppPaths.LogDirectory);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "worker-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "SyncForge Worker");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
    builder.Services.AddSingleton<IConfigStore>(serviceProvider =>
        SqliteConfigStore.CreateDefault(serviceProvider.GetRequiredService<ICredentialProtector>()));
    builder.Services.AddSingleton<IPostgresConnectionFactory, PostgresConnectionFactory>();
    builder.Services.AddSingleton<IPostgresSchemaReader, PostgresSchemaReader>();
    builder.Services.AddSingleton<ISourceGuardRail, SourceGuardRail>();
    builder.Services.AddSingleton<IPostgresSyncEngine, PostgresSyncEngine>();
    builder.Services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
    builder.Services.AddHostedService<SyncWorker>();

    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
