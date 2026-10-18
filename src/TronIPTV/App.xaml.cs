using System.Windows;
using System.Windows.Threading;
using TronIPTV.Views;

namespace TronIPTV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // Portable / test mode: keep settings and cache in a caller-chosen folder.
        if (Environment.GetEnvironmentVariable("TRONIPTV_DATA_DIR") is { Length: > 0 } dataDir)
        {
            Core.AppPaths.DataDir = dataDir;
            Core.AppPaths.CacheDir = Path.Combine(dataDir, "cache");
        }
        LibVLCSharp.Shared.Core.Initialize();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "TronIPTV — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
