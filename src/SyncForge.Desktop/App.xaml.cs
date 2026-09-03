using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SyncForge.Core.Configuration;
using SyncForge.Core.Postgres;
using SyncForge.Core.Storage;

namespace SyncForge.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
            collection.AddSingleton<IConfigStore>(provider =>
                SqliteConfigStore.CreateDefault(provider.GetRequiredService<ICredentialProtector>()));
            collection.AddSingleton<IPostgresConnectionFactory, PostgresConnectionFactory>();
            collection.AddSingleton<IPostgresSchemaReader, PostgresSchemaReader>();
            collection.AddSingleton<MainWindow>();
            _services = collection.BuildServiceProvider();

            await _services.GetRequiredService<IConfigStore>().InitializeAsync();
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"SyncForge tidak dapat dimulai.\n\n{exception.Message}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
