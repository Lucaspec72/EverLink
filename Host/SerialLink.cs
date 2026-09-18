using System.IO.Ports;
using System.Management;

namespace EverLinkHost;

/// <summary>A COM port plus its Windows device description, e.g. "COM5 (USB-SERIAL CH340)".</summary>
public record PortInfo(string PortName, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}

/// <summary>A confirmed EverLink Relay device: the COM port it's currently on, its
/// stable hardware identity (MAC address), its chip model, and the protocol version its
/// firmware reports - all obtained via the identification ping.</summary>
public record DeviceInfo(string PortName, string FriendlyName, string Mac, string ChipModel, int ProtocolVersion)
{
    /// <summary>The highest protocol version this build of Host actually knows about -
    /// i.e. the version EverLink_Protocol.md currently documents and KnownFeatures below
    /// is written against. NOT a hardcoded ceiling Host refuses to exceed: a Relay
    /// reporting a HIGHER version than this is still treated as this version for every
    /// feature check (see EffectiveProtocolVersion) - the working assumption is that a
    /// later protocol version is a superset of everything an earlier one guarantees, so
    /// Host can keep functioning as far as it understands, it just can't know about or use
    /// whatever a newer Relay might additionally offer. Bump this when Host is actually
    /// updated to understand a new protocol version's feature(s) - see KnownFeatures.</summary>
    public const int HighestKnownProtocolVersion = 2;

    /// <summary>Whether this specific chip model has the native USB OTG peripheral needed
    /// to output as a USB HID gamepad the console can see. Plain ESP32 and C3-family chips
    /// lack this in hardware - no firmware trick can add it. S2/S3/P4-family chips have it.
    /// This is a static lookup on chip family, not a runtime probe, since USB capability
    /// is fixed per silicon model.</summary>
    public bool IsUsbCapable => ChipModel.Contains("S2", StringComparison.OrdinalIgnoreCase)
                              || ChipModel.Contains("S3", StringComparison.OrdinalIgnoreCase)
                              || ChipModel.Contains("P4", StringComparison.OrdinalIgnoreCase);

    /// <summary>The protocol version to actually use for feature gating. Equal to
    /// ProtocolVersion normally; clamped down to HighestKnownProtocolVersion when the
    /// Relay reports something newer than this Host build understands - see
    /// IsNewerThanKnown. A hypothetical v3 Relay is treated as a v2 for every
    /// FeatureRequirement check below: this Host has no idea what v3 might add, but a
    /// later protocol version is assumed to still support everything an earlier one did
    /// (see HighestKnownProtocolVersion's doc comment), so "treat it as the newest version
    /// I understand" is a reasonable, working default rather than refusing to talk to it
    /// at all.</summary>
    public int EffectiveProtocolVersion => Math.Min(ProtocolVersion, HighestKnownProtocolVersion);

    /// <summary>True when this Relay's firmware reports a protocol version newer than
    /// this Host build knows about. Not itself a feature check - see
    /// EffectiveProtocolVersion for how feature gating handles this case - just the flag
    /// that drives the "might be missing something, proceeding anyway" note in
    /// CompatibilityIssues below.</summary>
    public bool IsNewerThanKnown => ProtocolVersion > HighestKnownProtocolVersion;

    /// <summary>One named feature Host gates behind a minimum protocol version and/or a
    /// hardware requirement, plus the message to show when a given Relay doesn't meet it -
    /// see CompatibilityIssues below, which is just this table filtered down to whichever
    /// entries a given Relay fails. Centralizing every feature's requirement here (rather
    /// than a separate hand-written bool + if-check per feature, which is what this used
    /// to be) means adding a new version-gated or hardware-gated feature later is one more
    /// row in this list, not a new property AND a new branch in CompatibilityIssues that
    /// could be added in one place and forgotten in the other.</summary>
    private readonly record struct FeatureRequirement(
        int MinProtocolVersion,
        bool RequiresUsbOtg,
        Func<DeviceInfo, string> MissingMessage);

    private static readonly IReadOnlyList<FeatureRequirement> KnownFeatures = new[]
    {
        new FeatureRequirement(
            MinProtocolVersion: 2,
            RequiresUsbOtg: false,
            MissingMessage: d => $"EverLink firmware v{d.ProtocolVersion} doesn't support rumble."),

        new FeatureRequirement(
            MinProtocolVersion: 1,
            RequiresUsbOtg: true,
            MissingMessage: d => $"{d.ChipModel} does not have USB OTG, impossible to use wired bridge."),
    };

    /// <summary>Whether this Relay's firmware speaks protocol v2 or later - the version
    /// that added rumble forwarding (RMBL: lines - see EverLink_Protocol.md's "Rumble"
    /// section). Checked against EffectiveProtocolVersion (not the raw ProtocolVersion),
    /// so a Relay newer than this Host understands is still treated as supporting rumble,
    /// matching how the wire-level RMBL: parsing itself doesn't care about the exact
    /// version number, only whether it's new enough. Kept as its own named property
    /// (rather than folded only into the generic KnownFeatures table above) because
    /// SerialLink's rumble-line parsing needs a plain runtime check to branch on, which is
    /// a different job from producing a human-readable list of what's missing.</summary>
    public bool SupportsRumble => EffectiveProtocolVersion >= 2;

    /// <summary>Human-readable list of known feature-compatibility gaps for this specific
    /// Relay - each one worded as an informational note about what this Relay/board can't
    /// do, not as a hard error, since a v1 firmware or a non-USB-capable board are still
    /// usable Relays for whatever they DO support. Surfaced in the main window via a
    /// small warning button next to each Relay row that has any (see RelayRow in
    /// MainWindow.xaml.cs and RelayIssuesWindow) - a Relay with no issues gets no button
    /// at all, so this list is also what decides whether that button appears.
    ///
    /// Built from two sources: KnownFeatures above (generic, version/hardware-gated
    /// requirements - one entry per feature, no per-feature code needed beyond adding a
    /// row) plus, first, a standalone note when IsNewerThanKnown is true. That note isn't
    /// itself a "missing feature" the way the others are - quite the opposite, the Relay
    /// might have MORE than this Host knows how to use - so it doesn't fit the
    /// MinProtocolVersion/RequiresUsbOtg shape KnownFeatures rows use and is handled
    /// separately here instead of being forced into that table.
    ///
    /// Worded generically ("firmware v1 doesn't support rumble", not "your firmware is
    /// too old, update it") rather than assuming the *reason* is staleness - a future
    /// Relay firmware variant could legitimately choose not to implement the USB-output
    /// role at all (e.g. one that relays over Bluetooth instead), in which case
    /// IsUsbCapable being false wouldn't be a bug to fix so much as a different design
    /// this specific firmware build made. This list just reports what a given Relay does
    /// and doesn't support, without editorializing about why.</summary>
    public IReadOnlyList<string> CompatibilityIssues
    {
        get
        {
            var issues = new List<string>();

            if (IsNewerThanKnown)
                issues.Add(
                    $"This Relay reports protocol v{ProtocolVersion}, newer than the v{HighestKnownProtocolVersion} " +
                    "this version of Host knows about. Attempting to use it as v" +
                    $"{HighestKnownProtocolVersion} - it should still work, but newer features (if any) won't be " +
                    "available, and there may be other issues.");

            foreach (var feature in KnownFeatures)
            {
                bool meetsVersion = EffectiveProtocolVersion >= feature.MinProtocolVersion;
                bool meetsHardware = !feature.RequiresUsbOtg || IsUsbCapable;
                if (!meetsVersion || !meetsHardware)
                    issues.Add(feature.MissingMessage(this));
            }

            return issues;
        }
    }
}

/// <summary>
/// The wire protocol shared with EverLink Relay firmware. Keep this in sync with the
/// firmware's packet parser - see EverLink_Protocol.md (repo root) for the authoritative,
/// up-to-date field layout and button bit assignments (not duplicated here, to avoid the
/// two descriptions drifting out of sync with each other over time).
/// </summary>
public static class PacketProtocol
{
    public const byte SyncByte = 0xA5;
    public const int PacketSize = 14; // sync(1) + buttons(2) + LT(1) + RT(1) + LX,LY,RX,RY(2 each=8) + checksum(1)

    public static byte[] Encode(in ControllerState s)
    {
        var buf = new byte[PacketSize];
        int i = 0;
        buf[i++] = SyncByte;

        buf[i++] = (byte)(s.Buttons & 0xFF);
        buf[i++] = (byte)(s.Buttons >> 8);

        buf[i++] = s.LeftTrigger;
        buf[i++] = s.RightTrigger;

        WriteInt16(buf, ref i, s.LeftX);
        WriteInt16(buf, ref i, s.LeftY);
        WriteInt16(buf, ref i, s.RightX);
        WriteInt16(buf, ref i, s.RightY);

        // Simple XOR checksum over everything after the sync byte - cheap to verify on the
        // Relay side and good enough to catch a corrupted/misaligned packet on a wired link.
        byte checksum = 0;
        for (int j = 1; j < i; j++) checksum ^= buf[j];
        buf[i++] = checksum;

        return buf;
    }

    private static void WriteInt16(byte[] buf, ref int i, short value)
    {
        buf[i++] = (byte)(value & 0xFF);
        buf[i++] = (byte)((value >> 8) & 0xFF);
    }
}

/// <summary>
/// Owns one serial connection to one EverLink Relay and streams controller state to it on a timer.
/// One of these per (controller, COM port) combo currently in use.
/// </summary>
public class SerialLink : IDisposable
{
    private readonly SerialPort _port;
    private System.Threading.Timer? _sendTimer;
    private SdlControllerReader? _source;
    private readonly object _lock = new();

    /// <summary>Optional remap profile applied to the raw controller state before sending. Null = passthrough.</summary>
    public RemapProfile? Remap { get; set; }

    public string PortName { get; }
    public bool IsOpen => _port.IsOpen;
    public long PacketsSent { get; private set; }
    public string? LastError { get; private set; }

    // Packets-per-second: a simple rolling counter reset once a second, giving a live
    // "is this actually moving" signal beyond a monotonically-climbing total. Read by the
    // UI on its own refresh timer; not attempting sub-second precision since this is a
    // human-facing indicator, not a diagnostic measurement.
    private long _packetsSentThisWindow;
    private DateTime _windowStart = DateTime.UtcNow;
    public int PacketsPerSecond { get; private set; }

    // Set whenever the Relay actually sends something back (a debug line, a ping reply) -
    // a stronger connectivity signal than "the OS says the COM port handle is open", which
    // is also true for a cable that's been unplugged but whose old handle hasn't errored
    // out yet. Null until at least one line has ever been received.
    public DateTime? LastReceivedFromRelay { get; private set; }

    // How long since the last received line before we stop treating the Relay as
    // "confirmed responsive" and fall back to just reporting the port's open/closed state.
    private static readonly TimeSpan RelayResponsiveTimeout = TimeSpan.FromSeconds(3);
    public bool IsRelayConfirmedResponsive =>
        LastReceivedFromRelay is { } last && DateTime.UtcNow - last < RelayResponsiveTimeout;

    // 250Hz send rate - well above what USB polling on either end can actually use,
    // but cheap and leaves no perceptible input queuing.
    private const int SendIntervalMs = 4;

    // Manual receive buffer for accumulating partial lines across DataReceived events -
    // see OnDataReceived's doc comment for why this replaces SerialPort.ReadLine().
    private readonly List<byte> _rxBuffer = new();
    private const int MaxRxBufferBytes = 4096; // generous multiple of one debug line's length

    // ---- Rumble keep-alive state ----
    //
    // Relay only sends a RMBL: line when the motor levels actually CHANGE (see
    // EverLink_Protocol.md's "Rumble" section) - it does not re-send the same value
    // repeatedly while a rumble is being held on. But SDL_RumbleGamepad's own duration
    // parameter is a one-shot timer on the SDL/OS side: passing durationMs=150 once and
    // then never calling it again means the physical controller stops buzzing after
    // 150ms even though the console (via Relay) still considers rumble "on", because nothing
    // ever told SDL to keep going. A game like Dolphin sends one "start rumbling" command
    // and expects it to persist until an explicit "stop" - it doesn't re-issue "still
    // rumbling" pings every frame, so Relay's own RMBL: line (mirroring that same
    // start/stop model) doesn't either.
    //
    // The fix: remember the last rumble state we were told about, and re-arm SDL's
    // duration ourselves on a steady cadence for as long as that state is non-zero -
    // effectively translating Relay's "start/stop" model into the repeated "still going"
    // pokes SDL's one-shot timer actually needs. This keep-alive stops re-arming (and lets
    // the physical rumble decay naturally) once the state goes back to 0:0, or if this link
    // stops hearing from Relay entirely (see IsRelayConfirmedResponsive) - a disconnected
    // Relay has no way to ever send an explicit "stop", so timing out avoids a rumble that
    // would otherwise be stuck on forever.
    // volatile since these are written from OnDataReceived's thread (via
    // TryHandleRumbleLine) and read from OnTick's timer thread - a plain byte assignment
    // is already atomic, so volatile here just ensures the writing thread's update is
    // actually visible to the reading thread promptly rather than potentially cached.
    // _lastRumbleKeepAliveSentUtc is a DateTime (not a single atomically-assignable
    // primitive), so it's guarded by _lock instead - see RearmRumbleKeepAliveIfDue and
    // TryHandleRumbleLine, both of which take _lock only around this one field's
    // read/write, not the whole rumble-handling path.
    private volatile byte _rumbleLeft;
    private volatile byte _rumbleRight;
    private DateTime _lastRumbleKeepAliveSentUtc = DateTime.MinValue;

    // Re-arms SDL_RumbleGamepad's duration well before that duration would actually
    // expire (150ms, see SdlControllerReader.Rumble's default) - a comfortable margin
    // under that so a tick that's briefly delayed under load doesn't let a real gap
    // through and cause an audible/felt stutter in the rumble.
    private static readonly TimeSpan RumbleKeepAliveInterval = TimeSpan.FromMilliseconds(80);

    public SerialLink(string portName, int baudRate = 921600)
    {
        PortName = portName;
        _port = new SerialPort(portName, baudRate)
        {
            WriteTimeout = 50,
            ReadTimeout = 50,
        };
    }

    public void Open()
    {
        try
        {
            _port.Open();
            _port.DataReceived += OnDataReceived;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    private void OnDataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        // Reads whatever raw bytes are currently available and splits them into lines
        // manually, rather than calling SerialPort.ReadLine(). ReadLine() is a known
        // source of exactly the "updates late, or not at all" symptom this replaces:
        // it's a blocking call with its own internal buffering, and on Windows the
        // DataReceived event's firing cadence isn't tightly coupled to when a full line
        // has actually arrived - a line can sit fully received in the OS driver's buffer
        // for a while before ReadLine() is called again to pick it up, especially on a
        // busy high-baud-rate link like this one (921600 baud, constant 250Hz writes in
        // the other direction). Reading raw bytes with Read() and finding newlines
        // ourselves has no such internal blocking/timeout behavior to get stuck in - it's
        // just "grab whatever bytes are sitting there right now" every time the event
        // fires, which is exactly what DataReceived already promises us.
        try
        {
            var chunk = new byte[Math.Max(1, _port.BytesToRead)];
            while (_port.IsOpen && _port.BytesToRead > 0)
            {
                int toRead = Math.Min(chunk.Length, _port.BytesToRead);
                int n = _port.Read(chunk, 0, toRead);
                if (n <= 0) break;

                for (int i = 0; i < n; i++)
                {
                    byte b = chunk[i];
                    if (b == (byte)'\n')
                    {
                        ProcessCompleteLine();
                    }
                    else if (b != (byte)'\r') // drop CR outright - never meaningful on its own
                    {
                        _rxBuffer.Add(b);
                        // Safety valve: if something has gone wrong upstream (garbled
                        // baud rate, a firmware that never sends '\n') and bytes just
                        // keep piling up with no line ending in sight, drop the buffer
                        // rather than growing it forever - we'll resync on the next '\n'
                        // that does eventually arrive.
                        if (_rxBuffer.Count > MaxRxBufferBytes) _rxBuffer.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Prefix of the one line Host actually parses out of everything Relay sends -
    /// see EverLink_Protocol.md's "Rumble" section. Only sent by v2+ firmware (never by
    /// v1), and only when the motor levels change.</summary>
    private const string RumblePrefix = "RMBL:";

    /// <summary>Marks a completed line's worth of bytes in _rxBuffer as received and clears
    /// the buffer for the next one. Called whenever a '\n' byte is seen in OnDataReceived.
    /// Receiving any line at all is used as a liveness signal, to update
    /// LastReceivedFromRelay below, regardless of its content. The only line content Host
    /// actually parses is the "RMBL:" rumble line (see RumblePrefix) - the firmware's
    /// periodic debug summary, including its "USBEnumerated:yes/no" field - see
    /// "EverLink Relay.ino" - is meant for a serial monitor, not parsed here. See
    /// DeviceInfo.CompatibilityIssues's doc comment for why Host doesn't try to surface
    /// USB-enumeration state as a trusted status indicator.</summary>
    private void ProcessCompleteLine()
    {
        if (_rxBuffer.Count == 0) return; // blank line (e.g. a lone \r\n) - nothing to report

        // Decode before clearing - _rxBuffer is what actually holds this line's bytes.
        // ASCII is enough for everything Relay ever sends on this line; using ASCII rather
        // than UTF8 here also means a stray non-ASCII byte from a corrupted/misaligned
        // read can't throw a decoding exception on what's otherwise just a liveness ping.
        string line;
        try { line = System.Text.Encoding.ASCII.GetString(_rxBuffer.ToArray()); }
        catch { line = string.Empty; }

        _rxBuffer.Clear();
        LastReceivedFromRelay = DateTime.UtcNow;

        if (line.StartsWith(RumblePrefix, StringComparison.Ordinal))
            TryHandleRumbleLine(line.Substring(RumblePrefix.Length));
    }

    /// <summary>Parses a rumble line's payload ("<left>:<right>", each 0-255 - see
    /// EverLink_Protocol.md) and plays it on whichever controller is currently assigned to
    /// this link, if any. Silently ignores anything malformed - a corrupted/misaligned
    /// read on this line shouldn't do anything worse than "no rumble this time", the same
    /// tolerant spirit as the main packet protocol's checksum-drop behavior.</summary>
    private void TryHandleRumbleLine(string payload)
    {
        var parts = payload.Split(':', 2);
        if (parts.Length != 2)
        {
            return;
        }
        if (!byte.TryParse(parts[0], out var left))
        {
            return;
        }
        if (!byte.TryParse(parts[1], out var right))
        {
            return;
        }

        SdlControllerReader? src;
        lock (_lock) { src = _source; }

        // Remember this as the current rumble state so OnTick's keep-alive (see the
        // "Rumble keep-alive state" fields above) can keep re-arming SDL's one-shot
        // duration timer for as long as it stays non-zero - Relay itself won't send
        // another RMBL: line until the state changes again, so this is the only record
        // of "what should currently be rumbling" Host has between now and the next change.
        _rumbleLeft = left;
        _rumbleRight = right;
        lock (_lock) { _lastRumbleKeepAliveSentUtc = DateTime.UtcNow; } // this call below counts as the first "keep-alive" tick

        src?.Rumble(left, right);
    }

    /// <summary>The controller currently assigned to this Relay, or null if none is
    /// assigned - a Relay can exist and be connected with no controller assigned at all.</summary>
    public SdlControllerReader? Source
    {
        get { lock (_lock) return _source; }
    }

    /// <summary>Begins streaming this Relay's link, optionally with a controller already
    /// assigned. The timer starts regardless of whether a controller is given - see
    /// OnTick(), which simply does nothing on a tick if _source is null. This is what lets
    /// a Relay "just exist" as soon as it's found, with controller assignment as a
    /// separate, later, optional step (see SetController) rather than a precondition for
    /// the Relay being usable at all.</summary>
    public void StartStreaming(SdlControllerReader? source = null)
    {
        lock (_lock)
        {
            _source = source;
            _sendTimer ??= new System.Threading.Timer(OnTick, null, 0, SendIntervalMs);
        }
    }

    /// <summary>Assigns (or clears, with null) the controller feeding this Relay, without
    /// interrupting the send timer - used by the Relay configuration window's controller
    /// dropdown to swap controllers on a live connection.</summary>
    public void SetController(SdlControllerReader? source)
    {
        lock (_lock) { _source = source; }
    }

    public void StopStreaming()
    {
        System.Threading.Timer? timer;
    
        lock (_lock)
        {
            timer = _sendTimer;
            _sendTimer = null;
            _source = null;
        }
    
        timer?.Dispose();
    }

    private void OnTick(object? state)
    {
        SdlControllerReader? src;
        lock (_lock) { src = _source; }
        if (src is null || !_port.IsOpen) return;

        // Poll happens here rather than in a separate loop so each link reads the freshest
        // state right before it sends - avoids extra buffering/latency between poll and send.
        // Note: this runs on a background timer thread, NOT the UI thread - we deliberately
        // don't call SdlSubsystem.PumpEvents() from here. SDL_PumpEvents() should only be
        // called from the thread that initialized SDL (the UI thread does this via its own
        // preview timer, every 50ms), so this method only READS already-pumped gamepad
        // state via SDL_GetGamepadButton/Axis, which is documented as safe from any thread.
        src.Poll();
        var stateToSend = Remap?.Apply(src.LastState) ?? src.LastState;
        var packet = PacketProtocol.Encode(stateToSend);

        try
        {
            _port.Write(packet, 0, packet.Length);
            PacketsSent++;

            _packetsSentThisWindow++;
            var elapsed = DateTime.UtcNow - _windowStart;
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                // Scale by actual elapsed time rather than assuming exactly 1s passed -
                // this timer callback can be delayed under system load, and a raw count
                // over a slightly-longer-than-1s window would under-report the true rate.
                PacketsPerSecond = (int)(_packetsSentThisWindow / elapsed.TotalSeconds);
                _packetsSentThisWindow = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // Deliberately not throwing here - a transient write failure (e.g. cable hiccup)
            // shouldn't tear down the timer. The UI polls LastError to surface this.
        }

        RearmRumbleKeepAliveIfDue(src);
    }

    /// <summary>Re-fires SDL_RumbleGamepad on a steady cadence (RumbleKeepAliveInterval)
    /// for as long as the last known rumble state (set by TryHandleRumbleLine) is
    /// non-zero - see the "Rumble keep-alive state" fields' doc comment above for why
    /// this exists at all: SDL's rumble duration is a one-shot timer, but Relay only
    /// tells us about CHANGES, not "still going" pings, so without this a held rumble
    /// would stop after SdlControllerReader.Rumble's ~150ms default no matter how long the
    /// console actually wanted it held.
    ///
    /// Called from OnTick (every SendIntervalMs = 4ms) rather than its own timer - this
    /// link already has a live 4ms tick going, and RumbleKeepAliveInterval's own check
    /// below is what actually rate-limits how often SDL_RumbleGamepad gets called, so
    /// this doesn't mean re-arming happens every 4ms, just that the interval is checked
    /// that often (cheap: two field reads and a DateTime comparison on every tick that
    /// doesn't fire).
    ///
    /// Also stops (implicitly, by simply not calling Rumble) once
    /// IsRelayConfirmedResponsive goes false - if Relay itself has gone silent (cable
    /// unplugged, board reset, etc.) there's no way to ever receive the explicit "stop"
    /// (RMBL:0:0) that would normally end this, so timing out here is what keeps a lost
    /// connection from leaving the physical controller buzzing forever.</summary>
    private void RearmRumbleKeepAliveIfDue(SdlControllerReader? src)
    {
        if (src is null) return;
        if (_rumbleLeft == 0 && _rumbleRight == 0) return; // nothing to keep alive
        if (!IsRelayConfirmedResponsive) return; // Relay's gone quiet - let any physical rumble decay rather than looping forever

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (now - _lastRumbleKeepAliveSentUtc < RumbleKeepAliveInterval) return;
            _lastRumbleKeepAliveSentUtc = now;
        }

        src.Rumble(_rumbleLeft, _rumbleRight);
    }

    public void Dispose()
    {
        StopStreaming();
        if (_port.IsOpen)
        {
            _port.DataReceived -= OnDataReceived;
            _port.Close();
        }
        _port.Dispose();
    }

    // Must match the firmware's PING_BYTE / IDENT_PREFIX exactly - see EverLink_Protocol.md.
    // Deliberately stops right after "v" (not "v1:" or "v2:") so this one prefix matches
    // every protocol version's ident reply - the version number itself is parsed out of
    // whatever follows, in TryPingDevice below, rather than needing a different constant
    // (or a guess-and-retry) per firmware version Host might encounter.
    private const byte PingByte = 0xFE;
    private const string IdentPrefix = "IAM:EverLink:v";
    private const int PingTimeoutMs = 300;

    /// <summary>
    /// Probes every currently-visible COM port with the identification ping and returns
    /// only the ones that reply as genuine EverLink Relay devices. This replaces plain
    /// port enumeration for the main device list - unrelated serial devices (mice, modems,
    /// anything not running our firmware) simply won't respond correctly and are excluded,
    /// so there's no need for a manual hide list.
    ///
    /// This briefly opens and closes each candidate port in turn, so it's slower than a
    /// plain GetPortNames() call - only call this on an explicit user-initiated rescan, not
    /// on a timer or automatically, to avoid repeatedly poking at ports that might belong
    /// to other running software (e.g. a mouse's receiver, a modem).
    /// </summary>
    public static List<DeviceInfo> ScanForEverLinkRelays(IEnumerable<string>? skipPorts = null)
    {
        var skip = new HashSet<string>(skipPorts ?? Enumerable.Empty<string>());
        var friendlyNames = ScanAvailablePortsWithNames().ToDictionary(p => p.PortName, p => p.FriendlyName);
        var found = new List<DeviceInfo>();

        foreach (var portName in SerialPort.GetPortNames())
        {
            if (skip.Contains(portName)) continue; // already open elsewhere (an existing Relay connection) - can't probe it, and don't need to

            var identity = TryPingDevice(portName);
            if (identity is not null)
            {
                var friendly = friendlyNames.TryGetValue(portName, out var f) ? f : portName;
                found.Add(new DeviceInfo(portName, friendly, identity.Value.Mac, identity.Value.ChipModel, identity.Value.ProtocolVersion));
            }
        }

        return found;
    }

    /// <summary>Opens the given port, sends the identification ping, and returns the
    /// replying device's (ProtocolVersion, Mac, ChipModel) if it responds correctly within
    /// the timeout - or null if the port couldn't be opened, didn't reply in time, or
    /// replied with something unexpected (including an older firmware build that only
    /// sends the MAC with no chip model suffix - treated as "unknown model" rather than a
    /// parse failure).</summary>
    private static (int ProtocolVersion, string Mac, string ChipModel)? TryPingDevice(string portName)
    {
        try
        {
            using var probe = new SerialPort(portName, 921600)
            {
                ReadTimeout = PingTimeoutMs,
                WriteTimeout = PingTimeoutMs,
            };
            probe.Open();

            // Clear out any stale bytes sitting in the buffer from before we opened, so we
            // don't misread leftover data as our reply.
            probe.DiscardInBuffer();

            probe.Write(new byte[] { PingByte }, 0, 1);

            string? line = probe.ReadLine(); // throws TimeoutException if nothing arrives in time
            line = line?.TrimEnd('\r', '\n');

            if (line is null || !line.StartsWith(IdentPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            // "<N>:<MAC>:<ChipModel>" where N is the protocol version digit(s) - e.g.
            // "2:B0CBD8CCBEF0:ESP32". Split on ':' up to 3 parts so a MAC or chip model
            // string couldn't accidentally shift the split (neither ever contains a colon
            // in practice, but capping at 3 parts here costs nothing and avoids relying on
            // that).
            var rest = line.Substring(IdentPrefix.Length);
            var parts = rest.Split(':', 3);

            // A pre-v2 (v1) Relay's ident reply was "IAM:EverLink:v1:<MAC>:<ChipModel>" -
            // i.e. the "1" was baked into the old IdentPrefix constant itself, not a
            // separate field the way v2+ sends it. IdentPrefix now stops before that
            // digit, so a v1 reply's "rest" here starts with "1:<MAC>:<ChipModel>" - same
            // shape as a v2+ reply's "<N>:<MAC>:<ChipModel>", just always "1". No special
            // casing needed: the generic parse below handles both.
            if (parts.Length < 2 || !int.TryParse(parts[0], out var version))
            {
                return null; // not a recognizable ident reply at all
            }

            var mac = parts[1];
            var chipModel = parts.Length > 2 ? parts[2] : "Unknown";
            return (version, mac, chipModel);
        }
        catch
        {
            // Port busy, access denied, timeout, garbled reply - all treated the same way
            // for real callers: this port isn't a usable EverLink Relay device right now.
            return null;
        }
    }

    /// <summary>
    /// Scans COM ports along with their Windows device description (e.g. chip/board name),
    /// pulled via WMI from Win32_PnPEntity. Falls back to just the port name if WMI lookup
    /// fails for a given device (e.g. driver doesn't expose a friendly caption).
    /// </summary>
    public static List<PortInfo> ScanAvailablePortsWithNames()
    {
        var names = new Dictionary<string, string>(); // COMx -> friendly caption
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Caption FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var caption = obj["Caption"]?.ToString();
                if (string.IsNullOrEmpty(caption)) continue;

                // Caption looks like "Silicon Labs CP210x USB to UART Bridge (COM5)"
                int open = caption.LastIndexOf('(');
                int close = caption.LastIndexOf(')');
                if (open < 0 || close <= open) continue;

                var comPart = caption.Substring(open + 1, close - open - 1); // "COM5"
                names[comPart] = caption;
            }
        }
        catch
        {
            // WMI unavailable/blocked - callers just get plain port names below.
        }

        return SerialPort.GetPortNames()
            .Select(p => new PortInfo(p, names.TryGetValue(p, out var friendly) ? friendly : p))
            .ToList();
    }
}
