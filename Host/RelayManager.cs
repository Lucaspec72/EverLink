namespace EverLinkHost;

/// <summary>
/// A single detected EverLink Relay and everything about how it's currently being used -
/// its serial link (always open as soon as the Relay is found, connected or not) and
/// whichever controller (if any) is currently feeding it.
///
/// Deliberately NOT called "Pairing" - a Relay isn't paired with anything, it's just a
/// device that exists, the same way a mouse exists whether or not you're currently moving
/// it. Controller assignment is one mutable attribute of an existing Relay, not something
/// that has to happen before the Relay is real.
/// </summary>
public class RelayConnection
{
    public required DeviceInfo Device { get; init; }
    public required SerialLink Link { get; init; }

    /// <summary>Convenience accessor - the controller currently assigned, if any. Assign
    /// via RelayManager.SetController rather than setting this directly, so the
    /// last-used-controller memory (keyed by the Relay's MAC) stays in sync.</summary>
    public SdlControllerReader? Controller => Link.Source;
}

/// <summary>
/// Tracks every detected EverLink Relay for the lifetime of the app. Unlike the old
/// PairingManager, a RelayConnection is created the moment a Relay is found (via rescan)
/// and its serial link opens immediately - controller assignment is a separate, optional,
/// later step (see SetController), not a precondition for the connection existing.
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
        _relaysByMac[device.Mac] = relay;
        return relay;
    }

    public bool TryGetRelay(string mac, out RelayConnection relay) =>
        _relaysByMac.TryGetValue(mac, out relay!);

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
