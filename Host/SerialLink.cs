using System.IO.Ports;
using System.Management;

namespace EverLinkHost;

/// <summary>A COM port plus its Windows device description, e.g. "COM5 (USB-SERIAL CH340)".</summary>
public record PortInfo(string PortName, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}

/// <summary>A confirmed EverLink Relay device: the COM port it's currently on, its
/// stable hardware identity (MAC address), and its chip model, obtained via the
/// identification ping.</summary>
public record DeviceInfo(string PortName, string FriendlyName, string Mac, string ChipModel)
{
    /// <summary>Whether this specific chip model has the native USB OTG peripheral needed
    /// for the eventual USB-HID/console-facing role. Plain ESP32 and C3-family chips lack
    /// this in hardware - no firmware trick can add it. S2/S3/P4-family chips have it.
    /// This is a static lookup on chip family, not a runtime probe, since USB capability
    /// is fixed per silicon model.</summary>
    public bool IsUsbCapable => ChipModel.Contains("S2", StringComparison.OrdinalIgnoreCase)
                              || ChipModel.Contains("S3", StringComparison.OrdinalIgnoreCase)
                              || ChipModel.Contains("P4", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The wire protocol shared with EverLink Relay firmware. Keep this in sync with the
/// firmware's packet parser - see EverLink/PROTOCOL.md for the authoritative, up-to-date
/// field layout and button bit assignments (not duplicated here, to avoid the two
/// descriptions drifting out of sync with each other over time).
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
/// One of these per paired (controller, COM port) combo.
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

    // Rolling buffer of lines received from the Relay (e.g. its periodic debug
    // summaries). Capped so it can't grow unbounded over a long session.
    private readonly LinkedList<string> _receivedLines = new();
    private const int MaxBufferedLines = 500;
    private readonly object _logLock = new();

    public event Action<string>? LineReceived;

    public IReadOnlyList<string> GetRecentLines()
    {
        lock (_logLock) return _receivedLines.ToList();
    }

    // 250Hz send rate - well above what USB polling on either end can actually use,
    // but cheap and leaves no perceptible input queuing.
    private const int SendIntervalMs = 4;

    // Manual receive buffer for accumulating partial lines across DataReceived events -
    // see OnDataReceived's doc comment for why this replaces SerialPort.ReadLine().
    private readonly List<byte> _rxBuffer = new();
    private const int MaxRxBufferBytes = 4096; // generous multiple of one debug line's length

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

    /// <summary>Converts whatever's currently in _rxBuffer into a completed line, records
    /// it, and clears the buffer for the next one. Called whenever a '\n' byte is seen in
    /// OnDataReceived - CR bytes are already filtered out before reaching the buffer, so
    /// no trailing '\r' trimming is needed here.</summary>
    private void ProcessCompleteLine()
    {
        if (_rxBuffer.Count == 0) return; // blank line (e.g. a lone \r\n) - nothing to report

        string line = System.Text.Encoding.ASCII.GetString(_rxBuffer.ToArray());
        _rxBuffer.Clear();
        if (line.Length == 0) return;

        // Note: the firmware's human-readable summary line includes a raw
        // "USBEnumerated:yes/no" field from ESP32XInput.ready() (see EverLinkRelay.ino) -
        // it's not parsed out specially here, just shows up in the Device Console like
        // any other debug line below, for anyone who wants to look at it with the caveat
        // in mind that it can't be trusted to reflect the console's current physical
        // connection state. See MainWindow.UsbCapabilityLabel's doc comment for why Host
        // doesn't try to surface it as a trusted status indicator.

        lock (_logLock)
        {
            _receivedLines.AddLast(line);
            while (_receivedLines.Count > MaxBufferedLines) _receivedLines.RemoveFirst();
        }
        LastReceivedFromRelay = DateTime.UtcNow;
        LineReceived?.Invoke(line);
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

    // Must match the firmware's PING_BYTE / IDENT_PREFIX exactly - see PROTOCOL.md.
    private const byte PingByte = 0xFE;
    private const string IdentPrefix = "IAM:EverLink:v1:";
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
            if (skip.Contains(portName)) continue; // already open elsewhere (an active pairing) - can't probe it, and don't need to

            var identity = TryPingDevice(portName);
            if (identity is not null)
            {
                var friendly = friendlyNames.TryGetValue(portName, out var f) ? f : portName;
                found.Add(new DeviceInfo(portName, friendly, identity.Value.Mac, identity.Value.ChipModel));
            }
        }

        return found;
    }

    /// <summary>Opens the given port, sends the identification ping, and returns the
    /// replying device's (Mac, ChipModel) if it responds correctly within the timeout - or
    /// null if the port couldn't be opened, didn't reply in time, or replied with something
    /// unexpected (including an older firmware build that only sends the MAC with no chip
    /// model suffix - treated as "unknown model" rather than a parse failure).</summary>
    private static (string Mac, string ChipModel)? TryPingDevice(string portName)
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
                return null;

            var rest = line.Substring(IdentPrefix.Length); // "<MAC>:<ChipModel>" or just "<MAC>" on older firmware
            var parts = rest.Split(':', 2);
            var mac = parts[0];
            var chipModel = parts.Length > 1 ? parts[1] : "Unknown";
            return (mac, chipModel);
        }
        catch
        {
            // Port busy, access denied, timeout, garbled reply - all treated the same way:
            // this port isn't a usable EverLink Relay device right now.
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
