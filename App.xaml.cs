using System.Windows;
using EnshroudedServerManager.Core;

namespace EnshroudedServerManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer", "logs");
        AppLogger.Initialize(logDir);

        DispatcherUnhandledException += (s, ex) =>
        {
            AppLogger.Error($"Unhandled exception: {ex.Exception}");
            MessageBox.Show(
                $"An unexpected error occurred:\n{ex.Exception.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        base.OnStartup(e);
    }
}
