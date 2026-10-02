namespace EverLinkHost;

/// <summary>
/// A single detected EverLink Relay and everything about how it's currently being used -
/// its serial link (always open as soon as the Relay is found, connected or not) and
/// whichever controller (if any) is currently feeding it. Controller assignment is one
/// mutable attribute of an existing Relay, not something that has to happen before the
/// Relay is real - a Relay exists as soon as it's detected, the same way a mouse exists
/// whether or not you're currently moving it.
/// </summary>
public class RelayConnection
{
    // Backing field for Device - not a plain auto-property because Device's
    // ActiveModeIndex needs to be kept in sync with whatever the Relay itself last
    // confirmed via a MODE: line (DeviceInfo is an immutable record, so "updating" it
    // means replacing this field with a `with` copy - see SerialLink.ModeAcknowledged).
    // Object-initializer property order isn't guaranteed to match declaration order, so
    // wiring the event subscription can't safely happen in Device's own init accessor
    // (Link might not be assigned yet) - see WireModeSync, called explicitly by
    // RelayManager.AddRelay once both properties are definitely set instead.
    private DeviceInfo _device = null!;

    public required DeviceInfo Device
    {
        get => _device;
        init => _device = value;
    }

    public required SerialLink Link { get; init; }

    /// <summary>Convenience accessor - the controller currently assigned, if any. Assign
    /// via RelayManager.SetController rather than setting this directly, so the
    /// last-used-controller memory (keyed by the Relay's MAC) stays in sync.</summary>
    public SdlControllerReader? Controller => Link.Source;

    /// <summary>Subscribes Device's ActiveModeIndex/PairingState to stay in sync with
    /// Link's ModeAcknowledged/PairingStatusChanged events. Must be called once, after
    /// both Device and Link are set - see RelayManager.AddRelay, the only place a
    /// RelayConnection is constructed.</summary>
    public void WireModeSync()
    {
        Link.ModeAcknowledged += index =>
        {
            // Switching to a mode confirmed to be non-wireless resets any pairing status
            // left over from a previously-active wireless mode back to Idle - see
            // EverLink_Protocol.md section 6's "Host behavior notes" on stale PAIR: lines.
            // A wireless mode's own status is left as whatever it already was (typically
            // Idle from construction, or whatever the Relay most recently reported) until
            // a fresh PAIR: line updates it for real - this handler doesn't invent one.
            var modes = _device.EffectiveModes;
            var stillWireless = index >= 0 && index < modes.Count && modes[index].IsWireless;

            _device = stillWireless
                ? _device with { ActiveModeIndex = index }
                : _device with { ActiveModeIndex = index, PairingState = PairingState.Idle };
        };

        Link.PairingStatusChanged += state =>
        {
            // Only applied while Host currently believes the active mode is wireless -
            // a PAIR: line arriving just after Host's own record of the active mode
            // flipped to wired (but before that fact was true on the Relay's side, or
            // vice versa) is exactly the brief staleness window EverLink_Protocol.md
            // section 6 describes, and is dropped here rather than shown.
            if (!_device.IsPairingStatusMeaningful) return;
            _device = _device with { PairingState = state };
        };
    }
}

/// <summary>
/// Tracks every detected EverLink Relay for the lifetime of the app. A RelayConnection is
/// created the moment a Relay is found (via rescan) and its serial link opens immediately -
/// controller assignment is a separate, optional, later step (see SetController), not a
/// precondition for the connection existing.
/// </summary>
public class RelayManager
{
    private readonly Dictionary<string, RelayConnection> _relaysByMac = new();
    public IReadOnlyCollection<RelayConnection> Relays => _relaysByMac.Values;

    /// <summary>Opens a serial link for a newly-found Relay and starts streaming
    /// immediately with no controller assigned - the connection is usable/visible right
    /// away, controller assignment happens later via SetController.</summary>
    public RelayConnection AddRelay(DeviceInfo device, int baudRate = 921600)
    {
        var link = new SerialLink(device.PortName, baudRate) { Remap = RemapProfile.CreateDefault() };
        link.Open();
        link.StartStreaming(); // no controller yet - see SerialLink.StartStreaming's default

        var relay = new RelayConnection { Device = device, Link = link };
        relay.WireModeSync();
        _relaysByMac[device.Mac] = relay;
        return relay;
    }

    public bool TryGetRelay(string mac, out RelayConnection relay) =>
        _relaysByMac.TryGetValue(mac, out relay!);

    /// <summary>Asks a specific Relay (by MAC) to (re-)activate the mode at the given
    /// index in its advertised Modes list - see SerialLink.SwitchMode and
    /// EverLink_Protocol.md section 5. Returns false if the Relay isn't tracked or its
    /// link couldn't accept the write; the eventual confirmation (if any) arrives
    /// asynchronously via SerialLink.ModeAcknowledged, already wired to update
    /// RelayConnection.Device - callers don't need to do anything further with it.</summary>
    public bool SwitchMode(string relayMac, int modeIndex) =>
        _relaysByMac.TryGetValue(relayMac, out var relay) && relay.Link.SwitchMode(modeIndex);

    /// <summary>Assigns (or clears, with null) the controller feeding a specific Relay by
    /// MAC. Safe to call on a live connection - see SerialLink.SetController.</summary>
    public void SetController(string relayMac, SdlControllerReader? controller)
    {
        if (_relaysByMac.TryGetValue(relayMac, out var relay))
            relay.Link.SetController(controller);
    }

    public void RemoveRelay(string mac)
    {
        if (_relaysByMac.Remove(mac, out var relay))
        {
            try { relay.Link.Dispose(); }
            catch { /* best-effort during removal */ }
        }
    }

    public void RemoveAll()
    {
        foreach (var relay in _relaysByMac.Values.ToList())
        {
            try { relay.Link.Dispose(); }
            catch { /* continue shutting down the rest */ }
        }
        _relaysByMac.Clear();
    }
}
