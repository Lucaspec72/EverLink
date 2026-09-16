using System.Windows;

namespace EverLinkHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Initialize SDL once for the whole app's lifetime here rather than lazily in
        // MainWindow, so startup failures (e.g. SDL3.dll missing) surface immediately with
        // a clear message instead of showing up confusingly later during a rescan.
        try
        {
            SdlSubsystem.EnsureInitialized();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize SDL (controller support):\n\n{ex.Message}\n\n" +
                "Make sure SDL3.dll is present next to EverLinkHost.exe. " +
                "See HOST_BUILD_INSTRUCTIONS.txt for details.",
                "EverLink Host - Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SdlSubsystem.Shutdown();
        base.OnExit(e);
    }
}
