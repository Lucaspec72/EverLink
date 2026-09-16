using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EverLinkHost;

/// <summary>Row shown in RelayConfigureWindow's controller-assignment dropdown. A thin
/// bindable wrapper so the picker can bind to a real object (name, connection state)
/// instead of raw display strings.</summary>
public class ControllerRow : INotifyPropertyChanged
{
    public required uint InstanceId { get; init; }
    public required string Name { get; init; }

    private bool _isActive;
    /// <summary>True while this controller has recently received input - drives the
    /// activity indicator dot wherever this row is shown. Set by
    /// MainWindow.UpdateControllerActivityIndicators() whenever this controller's state
    /// changes, cleared by a short hold timer so the indicator holds briefly after the
    /// last input rather than blinking off instantly.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); } }
    }

    /// <summary>Last raw state seen for this controller, used only to detect "did anything
    /// change" for the activity indicator.</summary>
    public ControllerState? LastSeenState;

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Row shown in the main Relay list. Wraps a RelayConnection with the bindable
/// display properties the list template needs (labels, status text/color).</summary>
public class RelayRow : INotifyPropertyChanged
{
    public required RelayConnection Relay { get; init; }
    public required Func<string, string> ResolveNickname { get; init; }

    /// <summary>Looks up the ControllerRow (which carries the live IsActive flag) for
    /// whichever controller is currently assigned to this Relay, if any - set by
    /// MainWindow whenever the assignment changes or activity updates, since RelayRow
    /// itself doesn't own the controller-tracking dictionaries.</summary>
    public Func<uint, ControllerRow?>? ResolveControllerRow { get; init; }

    public bool ControllerIsActive =>
        Relay.Controller is { } reader && ResolveControllerRow?.Invoke(reader.InstanceId) is { IsActive: true };

    public string DeviceLabel
    {
        get
        {
            var mac = Relay.Device.Mac;
            var nick = ResolveNickname(mac);
            if (nick != mac) return nick;

            return mac.Length == 12
                ? string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))).ToLowerInvariant()
                : mac;
        }
    }

    public string ControllerLabel => Relay.Controller?.Name ?? "No controller assigned";

    /// <summary>Host<->Relay connection status. Three states rather than a plain
    /// bool: a Relay can have its serial port open (OS-level handle valid) without the
    /// firmware actually responding (e.g. a bad cable, or the board is mid-reset) - that
    /// distinction is worth surfacing rather than collapsing into one "connected" state
    /// that could be misleadingly green during a real problem.</summary>
    public string ConnectionStatusLabel
    {
        get
        {
            if (!Relay.Link.IsOpen) return "Disconnected";
            return Relay.Link.IsRelayConfirmedResponsive ? "Connected" : "Not responding";
        }
    }

    public Brush ConnectionStatusColor
    {
        get
        {
            if (!Relay.Link.IsOpen) return AccentRed;
            return Relay.Link.IsRelayConfirmedResponsive ? AccentGreen : AccentAmber;
        }
    }

    /// <summary>Whether this specific board's chip model supports acting as a real USB
    /// controller for the console at all (S2/S3/P4-family chips only) - named "USB
    /// Output" rather than just "USB" since the Host&lt;-&gt;Relay link is ALSO USB
    /// (serial-over-USB), so plain "USB" would be ambiguous between the two.
    ///
    /// Deliberately reports ONLY this fixed hardware fact (from DeviceInfo.IsUsbCapable,
    /// read off the chip model string during identification) - NOT whether the console
    /// is live-connected right now. An earlier version also tried to show a live
    /// ready/not-ready state parsed from the firmware's ESP32XInput.ready(), but that
    /// turned out to be unreliable: TinyUSB (the underlying USB stack) can't reliably
    /// detect a physical unplug for a bus-powered board without extra VBUS-sense wiring
    /// most boards don't have - see https://github.com/hathach/tinyusb/issues/2478. A
    /// status indicator that can silently go stale and keep claiming "Ready" after the
    /// cable's actually been pulled is worse than no live indicator at all, so it was
    /// removed rather than shipped as something that looks trustworthy but isn't. The
    /// firmware's human-readable debug summary still includes the same underlying
    /// ESP32XInput.ready() value as a raw "USBEnumerated:yes/no" field (visible to anyone
    /// watching this board's serial output directly, e.g. via a serial monitor, with that
    /// caveat in mind), but Host no longer parses it or surfaces it as a trusted status
    /// label.</summary>
    public string UsbCapabilityLabel
    {
        get
        {
            if (!Relay.Link.IsOpen) return "USB Output: Disconnected";
            return Relay.Device.IsUsbCapable ? "USB Output: Supported" : "USB Output: Unsupported";
        }
    }

    public Brush UsbCapabilityColor
    {
        get
        {
            if (!Relay.Link.IsOpen) return AccentRed;
            return Relay.Device.IsUsbCapable ? AccentGreen : AccentAmber;
        }
    }

    // Looked up lazily (not as a static field initializer) and loaded from Theme.xaml
    // directly rather than Application.Current.Resources - a static field initializer ran
    // the very first time ANY RelayRow was touched, which could happen before
    // MainWindow's own Window.Resources (where Theme.xaml gets merged in) had finished
    // loading, throwing a KeyNotFoundException that silently broke the whole binding
    // chain for the row (nothing displaying at all, not just wrong colors - this was the
    // actual cause of the status fields not showing anything).
    private static ResourceDictionary? _themeResources;
    private static ResourceDictionary ThemeResources => _themeResources ??=
        new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) };

    private static Brush AccentGreen => (Brush)ThemeResources["AccentGreen"];
    private static Brush AccentAmber => (Brush)ThemeResources["AccentAmber"];
    private static Brush AccentRed => (Brush)ThemeResources["AccentRed"];

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly RelayManager _relayManager = new();
    private readonly List<RelayRow> _relayRows = new();
    private readonly AppSettings _settings = AppSettings.Load();

    // All controllers currently seen by SDL - shared with any open Configure window's
    // controller dropdown, kept here (rather than per-window) since hotplug events need
    // one authoritative place to update.
    private readonly Dictionary<uint, SdlControllerReader> _scannedControllers = new();
    private readonly Dictionary<uint, ControllerRow> _controllerRows = new();

    private static readonly TimeSpan ActivityHoldDuration = TimeSpan.FromMilliseconds(600);
    private readonly Dictionary<uint, DateTime> _lastActivityAt = new();

    private DispatcherTimer? _activityTimer;
    private DispatcherTimer? _relayRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        WindowChromeHelper.EnableDarkTitleBar(this);

        // Subscribing before the first PumpEvents() call matters: SDL_INIT_GAMEPAD raises
        // an ADDED event for every gamepad already connected at init time, so this
        // subscription doubles as the initial controller scan.
        SdlSubsystem.GamepadAdded += OnGamepadAdded;
        SdlSubsystem.GamepadRemoved += OnGamepadRemoved;

        RescanRelaysButton_Click(this, new RoutedEventArgs());

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _activityTimer.Tick += (_, _) => { SdlSubsystem.PumpEvents(); UpdateControllerActivityIndicators(); };
        _activityTimer.Start();

        _relayRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _relayRefreshTimer.Tick += (_, _) => { foreach (var row in _relayRows) row.Refresh(); };
        _relayRefreshTimer.Start();

        Closing += MainWindow_Closing;
    }

    // ---------- Shutdown ----------

    private bool _isShuttingDown;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        SdlSubsystem.GamepadAdded -= OnGamepadAdded;
        SdlSubsystem.GamepadRemoved -= OnGamepadRemoved;

        _activityTimer?.Stop();
        _relayRefreshTimer?.Stop();

        _relayManager.RemoveAll();

        foreach (var reader in _scannedControllers.Values)
        {
            try { reader.Dispose(); }
            catch { /* shutdown continues regardless */ }
        }
        _scannedControllers.Clear();
    }

    // ---------- Controllers (auto-detected via SDL hotplug events) ----------

    private void OnGamepadAdded(uint instanceId)
    {
        if (_scannedControllers.ContainsKey(instanceId)) return;

        var reader = new SdlControllerReader(instanceId);
        _scannedControllers[instanceId] = reader;
        _controllerRows[instanceId] = new ControllerRow { InstanceId = instanceId, Name = reader.Name };

        // A Relay remembering this controller from a previous session (see
        // AppSettings.RelayMemories) gets auto-assigned it now that it's actually
        // available - this is the "start and forget" behavior: plug in the same
        // controller later and whichever Relay used it last picks it back up with no
        // manual re-selection needed.
        //
        // Matches on LastControllerIdentity (serial, or GUID+name model fingerprint - see
        // SdlControllerReader.BuildIdentity) when present, falling back to the older
        // LastControllerName-only comparison for a settings.json saved by a previous
        // EverLink Host build that predates Identity. Once this Relay is used again,
        // SaveRelayMemory populates LastControllerIdentity going forward - see its own
        // comment for why LastControllerName is still written too.
        var identity = reader.BuildIdentity();
        foreach (var relay in _relayManager.Relays)
        {
            if (relay.Controller is not null) continue; // already has one assigned, don't override
            if (!_settings.RelayMemories.TryGetValue(relay.Device.Mac, out var memory)) continue;

            bool matches = memory.LastControllerIdentity is { Length: > 0 } savedIdentity
                ? savedIdentity == identity
                : memory.LastControllerName == reader.Name;
            if (!matches) continue;

            _relayManager.SetController(relay.Device.Mac, reader);
        }

        RefreshRelayListDisplay();
    }

    private void OnGamepadRemoved(uint instanceId)
    {
        if (_scannedControllers.Remove(instanceId, out var reader))
        {
            // If any Relay was using this controller, clear the assignment rather than
            // leaving a stale/disposed reader behind - Controller/IsConnected downstream
            // would otherwise reference a reader that's about to be disposed.
            foreach (var relay in _relayManager.Relays)
            {
                if (relay.Controller == reader)
                    _relayManager.SetController(relay.Device.Mac, null);
            }

            try { reader.Dispose(); }
            catch { /* already gone */ }
        }
        _controllerRows.Remove(instanceId);
        _lastActivityAt.Remove(instanceId);

        RefreshRelayListDisplay();
    }

    /// <summary>Cheap pass over every known controller purely to detect "did this
    /// controller's input change since last check", for activity indicator dots (used by
    /// any open Configure window's controller picker).</summary>
    private void UpdateControllerActivityIndicators()
    {
        var now = DateTime.UtcNow;
        var changedInstanceIds = new HashSet<uint>();

        foreach (var (id, reader) in _scannedControllers)
        {
            if (!_controllerRows.TryGetValue(id, out var row)) continue;

            reader.Poll();
            var current = reader.LastState;
            var changed = row.LastSeenState is not { } prev || !StatesEqual(prev, current);
            row.LastSeenState = current;

            if (changed) _lastActivityAt[id] = now;

            var wasActive = row.IsActive;
            row.IsActive = _lastActivityAt.TryGetValue(id, out var lastAt) && now - lastAt < ActivityHoldDuration;
            if (wasActive != row.IsActive) changedInstanceIds.Add(id);
        }

        // RelayRow.ControllerIsActive is a separate computed property on a different
        // object than ControllerRow.IsActive - updating the latter doesn't automatically
        // notify anything bound to the former, so any Relay row whose assigned
        // controller's activity just flipped needs an explicit refresh here to actually
        // repaint its dot promptly, rather than waiting for the slower 500ms relay
        // refresh timer.
        if (changedInstanceIds.Count > 0)
        {
            foreach (var relayRow in _relayRows)
            {
                if (relayRow.Relay.Controller is { } controller && changedInstanceIds.Contains(controller.InstanceId))
                    relayRow.Refresh();
            }
        }
    }

    private static bool StatesEqual(in ControllerState a, in ControllerState b) =>
        a.Buttons == b.Buttons && a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger &&
        a.LeftX == b.LeftX && a.LeftY == b.LeftY && a.RightX == b.RightX && a.RightY == b.RightY;

    /// <summary>Exposes the live controller list/state to child windows (Configure) that
    /// need it for their own controller-selection dropdown - avoids each window having to
    /// duplicate SDL scanning/hotplug logic itself.</summary>
    public IReadOnlyDictionary<uint, SdlControllerReader> ScannedControllers => _scannedControllers;
    public IReadOnlyDictionary<uint, ControllerRow> ControllerRows => _controllerRows;

    // ---------- Relays (found via identification ping, always shown once found) ----------

    private string ResolveNickname(string mac) =>
        _settings.DeviceNicknames.TryGetValue(mac, out var nick) && !string.IsNullOrWhiteSpace(nick)
            ? nick
            : mac;

    private void RescanRelaysButton_Click(object sender, RoutedEventArgs e)
    {
        // Remove tracked Relays that are genuinely gone before looking for new ones -
        // without this, a disconnected Relay stayed tracked forever (its row just sat
        // there showing "Disconnected"), which also meant a replugged board with the same
        // MAC was silently skipped below as "already tracked" and could never reappear.
        // Two removal conditions, not just "port closed": a disconnected USB device's
        // SerialPort handle can keep reporting IsOpen==true for a while after the
        // physical device is actually gone (a known OS-level quirk), so we also remove a
        // Relay that's been unresponsive well past the normal "not responding" threshold -
        // long enough to be confident it's actually gone, not just a brief hiccup.
        var staleThreshold = TimeSpan.FromSeconds(10);
        var toRemove = _relayRows
            .Where(row => !row.Relay.Link.IsOpen ||
                          (row.Relay.Link.LastReceivedFromRelay is { } last && DateTime.UtcNow - last > staleThreshold))
            .ToList();

        foreach (var row in toRemove)
        {
            _relayManager.RemoveRelay(row.Relay.Device.Mac);
            _relayRows.Remove(row);
        }

        // Don't re-probe ports already owned by an existing (still-tracked) RelayConnection
        // - probing requires opening the port, which would fail (and isn't needed) for one
        // we already have open and are actively streaming on.
        var portsInUse = _relayRows.Select(r => r.Relay.Device.PortName);

        var found = SerialLink.ScanForEverLinkRelays(portsInUse);
        foreach (var device in found)
        {
            if (_relayManager.TryGetRelay(device.Mac, out _)) continue; // already tracked

            var relay = _relayManager.AddRelay(device);

            // Restore this Relay's remembered remap mapping, if any, from a previous
            // session - controller assignment is restored separately once/if that
            // controller actually shows up again (see OnGamepadAdded).
            if (_settings.RelayMemories.TryGetValue(device.Mac, out var memory) && relay.Link.Remap is { } profile)
            {
                foreach (var (targetName, source) in memory.RemapMapping)
                {
                    if (Enum.TryParse<RemapTarget>(targetName, out var target))
                        profile.Mapping[target] = source;
                }
            }

            var row = new RelayRow
            {
                Relay = relay,
                ResolveNickname = ResolveNickname,
                ResolveControllerRow = id => _controllerRows.GetValueOrDefault(id)
            };
            _relayRows.Add(row);
        }

        RefreshRelayListDisplay();
    }

    private void RefreshRelayListDisplay()
    {
        RelayListBox.ItemsSource = null;
        RelayListBox.ItemsSource = _relayRows;

        RelayEmptyText.Visibility = _relayRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private RelayRow? GetSelectedRelayRow() => RelayListBox.SelectedItem as RelayRow;

    private void RenameDevice_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRelayRow();
        if (row is null) return;

        var mac = row.Relay.Device.Mac;
        var currentName = _settings.DeviceNicknames.GetValueOrDefault(mac, "");
        var dialog = new InputDialog($"Nickname for device {mac} (currently on {row.Relay.Device.PortName}):", currentName) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            if (dialog.Result.Length == 0)
                _settings.DeviceNicknames.Remove(mac);
            else
                _settings.DeviceNicknames[mac] = dialog.Result;

            _settings.Save();
            row.Refresh();
        }
    }

    /// <summary>Persists a Relay's currently-assigned controller identity and remap
    /// mapping, called by RelayConfigureWindow whenever either changes - keeps AppSettings
    /// up to date live rather than only on app close, so a crash/force-quit doesn't lose
    /// recent changes.</summary>
    public void SaveRelayMemory(string relayMac)
    {
        if (!_relayManager.TryGetRelay(relayMac, out var relay)) return;

        var memory = _settings.RelayMemories.TryGetValue(relayMac, out var existing) ? existing : new RelayMemory();
        memory.LastControllerName = relay.Controller?.Name;
        memory.LastControllerIdentity = relay.Controller?.BuildIdentity();
        if (relay.Link.Remap is { } profile)
            memory.RemapMapping = profile.Mapping.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

        _settings.RelayMemories[relayMac] = memory;
        _settings.Save();
    }

    // ---------- Configure window ----------

    private void OpenConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RelayRow row }) return;

        // Non-modal, one per Relay - multiple Configure windows can be open at once for
        // different Relays.
        var configureWindow = new RelayConfigureWindow(row, this) { Owner = this };
        configureWindow.Closed += (_, _) => row.Refresh(); // controller assignment may have changed
        configureWindow.Show();
    }
}
