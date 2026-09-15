using System.IO;
using System.Text.Json;

namespace EverLinkHost;

/// <summary>
/// Tiny persisted settings file (JSON, next to the exe) so device nicknames survive app
/// restarts. Deliberately minimal - currently just the nickname map; see DeviceNicknames.
/// </summary>
public class AppSettings
{
    /// <summary>User-assigned nicknames, keyed by the device's stable MAC address (from the
    /// identification ping), e.g. "A4B2C3D4E5F6" -> "Player 1". MAC-keyed rather than
    /// COM-port-keyed so a nickname survives the board moving to a different COM port
    /// (e.g. plugged into a different USB socket later).</summary>
    public Dictionary<string, string> DeviceNicknames { get; set; } = new();

    /// <summary>Per-Relay memory of the last controller assigned and its remap mapping,
    /// keyed by the Relay's MAC. Restored automatically when that Relay is found again on
    /// a later run, so a Relay "just works" the same way it did last time without the user
    /// re-selecting a controller or redoing remaps each session.</summary>
    public Dictionary<string, RelayMemory> RelayMemories { get; set; } = new();

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - just start fresh rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't write (e.g. read-only folder), hiding just won't persist.
        }
    }
}

/// <summary>
/// What's remembered for one Relay across app restarts.
///
/// LastControllerIdentity matches by SdlControllerReader.BuildIdentity() - a serial number
/// when the controller/driver exposes one (PS4/PS5, Switch Pro, some others), otherwise a
/// GUID+name model fingerprint. Neither is a raw SDL instance ID, which isn't stable across
/// a restart or unplug/replug at all (a fresh one is assigned every time) - both Identity
/// forms survive that.
///
/// Honest limitation, carried over from the name-only scheme this replaced: when a
/// controller has no serial (most Xbox 360-style pads, including officially licensed
/// third-party ones), the GUID+name fallback still can't tell apart two identical units of
/// the same model - SDL itself assigns them the same GUID, full stop, and Name is no more
/// specific than the model. Whichever matching unit happens to be plugged in first wins.
/// This is strictly no worse than the old name-only matching for that case, and strictly
/// better for controllers that DO expose a serial - see SdlControllerReader.BuildIdentity's
/// doc comment for exactly how the two are layered.
///
/// LastControllerName is kept only so a settings.json written by an older EverLink Host
/// build still loads - AppSettings.Load() doesn't need any special migration code for it,
/// since a missing LastControllerIdentity naturally falls back to it (see MainWindow's
/// OnGamepadAdded). New saves always populate LastControllerIdentity; LastControllerName is
/// still written alongside it purely as a human-readable label if anyone opens
/// settings.json by hand - it is NOT read back once LastControllerIdentity is present.
/// </summary>
public class RelayMemory
{
    public string? LastControllerName { get; set; }
    public string? LastControllerIdentity { get; set; }
    public Dictionary<string, string> RemapMapping { get; set; } = new();
}
