using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EverLinkHost;

/// <summary>Native (DWM) window-chrome helpers shared by every top-level Window in the
/// app, so the dark title bar is one call - EnableDarkTitleBar(this) in the constructor,
/// hooked to SourceInitialized - rather than each window carrying its own copy of the
/// DllImport/constant/try-catch. Keeps MainWindow, RelayConfigureWindow and InputDialog's
/// (unfocused) title bars visually consistent with the rest of the app's dark theme
/// instead of falling back to the OS-default light title bar.</summary>
internal static class WindowChromeHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Hooks the given window's SourceInitialized event to switch its native
    /// title bar into dark mode as soon as its HWND exists (the DWM attribute can't be
    /// set before that). Safe to call from any Window's constructor right after
    /// InitializeComponent(). No-ops silently on Windows versions that don't support the
    /// attribute (pre-20H1) - this is purely cosmetic, so it's not worth failing the
    /// window over.</summary>
    public static void EnableDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int useDarkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            }
            catch
            {
                // Cosmetic only - fine to silently no-op on older Windows.
            }
        };
    }
}
