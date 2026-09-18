using SDL;

namespace EverLinkHost;

/// <summary>
/// Bit flags for our own outgoing button state, matching EverLink_Protocol.md's wire layout.
/// This is OUR bit assignment (not SDL's, not XInput's) - see EverLink_Protocol.md for the
/// authoritative list. Guide is the addition XInput could never give us.
/// </summary>
[Flags]
public enum ButtonBits : ushort
{
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400, // not reachable via XInput - only available since Host reads via SDL3
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>Snapshot of one controller's full input state, in the same shape we serialize
/// to the Relay. Trigger/stick ranges are normalized to match the existing wire protocol
/// (0-255 for triggers, signed 16-bit for sticks) even though SDL's own native ranges
/// differ - see SdlControllerReader.Poll() for the rescaling.</summary>
public struct ControllerState
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftX;
    public short LeftY;
    public short RightX;
    public short RightY;
}

/// <summary>
/// Owns SDL's subsystem lifecycle and gamepad enumeration for the whole app. SDL is
/// initialized once, globally - there's no per-controller SDL context, so this is a
/// static/singleton-style helper rather than an instance per reader.
/// </summary>
public static class SdlSubsystem
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public static void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_initialized) return;

            // GAMEPAD implies JOYSTICK internally; EVENTS lets SDL_GetGamepads() reflect
            // hotplugs without us having to manually poll a device-change API.
            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD | SDL_InitFlags.SDL_INIT_EVENTS))
                throw new InvalidOperationException($"SDL_Init failed: {SDL3.SDL_GetError()}");

            _initialized = true;
        }
    }

    /// <summary>Must be called once at app shutdown to release SDL's native resources cleanly.</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;
            SDL3.SDL_Quit();
            _initialized = false;
        }
    }

    /// <summary>
    /// SDL needs its event queue pumped periodically for hotplug detection and internal
    /// bookkeeping to work, even though we're not using SDL for windowing/rendering here.
    /// Call this regularly (e.g. from the same timer that drives the live preview).
    ///
    /// This also raises GamepadAdded/GamepadRemoved for any hotplug events found in the
    /// queue during this pump - callers that want live add/remove notifications should
    /// subscribe to those events rather than polling GetConnectedGamepadIds() on their own
    /// timer and diffing the results. Using SDL's actual events here (SDL_EVENT_GAMEPAD_
    /// ADDED/REMOVED) rather than periodic full-list polling is both the officially
    /// recommended approach and cheaper - one queue drain instead of repeated full-list
    /// enumeration and set-difference work every tick.
    /// </summary>
    public static void PumpEvents()
    {
        SDL3.SDL_PumpEvents();

        unsafe
        {
            SDL_Event ev;
            // SDL_PeepEvents drains only gamepad add/remove events from the queue, leaving
            // any other event types (which we don't use here, but shouldn't silently
            // swallow) for anyone else who might pump the queue.
            while (SDL3.SDL_PeepEvents(&ev, 1, SDL_EventAction.SDL_GETEVENT,
                       (uint)SDL_EventType.SDL_EVENT_GAMEPAD_ADDED,
                       (uint)SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED) > 0)
            {
                if (ev.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
                    GamepadAdded?.Invoke((uint)ev.gdevice.which);
                else if (ev.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED)
                    GamepadRemoved?.Invoke((uint)ev.gdevice.which);
            }
        }
    }

    /// <summary>Raised when PumpEvents() observes a gamepad connect - including once per
    /// already-connected gamepad the moment SDL_Init() first runs, so callers don't need a
    /// separate initial-scan path alongside their event subscription.</summary>
    public static event Action<uint>? GamepadAdded;

    /// <summary>Raised when PumpEvents() observes a gamepad disconnect.</summary>
    public static event Action<uint>? GamepadRemoved;

    /// <summary>Returns the SDL joystick instance IDs of all currently connected gamepads.</summary>
    public static List<uint> GetConnectedGamepadIds()
    {
        EnsureInitialized();
        unsafe
        {
            int count = 0;
            SDL_JoystickID* ids = SDL3.SDL_GetGamepads(&count);
            if (ids == null) return new List<uint>();

            var result = new List<uint>(count);
            for (int i = 0; i < count; i++)
                result.Add((uint)ids[i]);

            SDL3.SDL_free(ids);
            return result;
        }
    }

    /// <summary>Human-readable name for a gamepad, safe to call even before it's opened.</summary>
    public static string GetGamepadName(uint instanceId)
    {
        string? name = SDL3.SDL_GetGamepadNameForID((SDL_JoystickID)instanceId);
        return name ?? $"Controller {instanceId}";
    }

    /// <summary>SDL's implementation-dependent GUID for this gamepad model - encodes bus
    /// type, vendor/product ID, and driver, and is stable across replugs/reboots for a
    /// given MODEL on a given machine. Deliberately NOT treated as a per-unit identity
    /// anywhere in this file: SDL's own docs are explicit that two identical controllers
    /// from the same vendor/product/revision report the SAME GUID - this is a model
    /// fingerprint, not a serial number. Safe to call before the gamepad is opened.</summary>
    public static string GetGamepadGuid(uint instanceId)
    {
        // NOTE: mirrors the native signature (SDL_GUID guid, char *pszGUID, int cbGUID) ->
        // void, writing a 32-hex-char + null-terminator string into the caller's buffer.
        // Same "worth a quick sanity check against the pinned package version" caveat as
        // SDL_GetGamepadSerial above applies here too.
        var guid = SDL3.SDL_GetGamepadGUIDForID((SDL_JoystickID)instanceId);
        unsafe
        {
            var buf = stackalloc byte[33]; // SDL's own guidance: 32 hex chars + null terminator
            SDL3.SDL_GUIDToString(guid, buf, 33);
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)buf) ?? "";
        }
    }
}

/// <summary>
/// Opens and polls one SDL gamepad by its instance ID. Instance IDs are assigned by SDL
/// as devices connect and are stable for the lifetime of that connection (unlike XInput's
/// fixed 0-3 slots), but WILL change if the controller is unplugged and replugged - this
/// is why AppSettings.RelayMemories matches a remembered controller by a persisted
/// Identity string (see BuildIdentity below) rather than instance ID across app restarts.
/// </summary>
public class SdlControllerReader : IDisposable
{
    public uint InstanceId { get; }
    public bool IsConnected { get; private set; }
    public ControllerState LastState { get; private set; }
    public string Name { get; }

    /// <summary>Per-unit serial number, if this controller/driver exposes one (PS4/PS5
    /// DualShock/DualSense and Switch Pro controllers typically do; most Xbox 360-style
    /// pads, including officially licensed third-party ones, do not - the USB descriptor
    /// field is simply absent or hardcoded identically across every unit off the same
    /// assembly line). Null when unavailable - NOT a placeholder/empty string, so callers
    /// can tell "no serial exists" apart from "serial happens to be blank".</summary>
    public string? Serial { get; }

    /// <summary>SDL's model-level GUID - see SdlSubsystem.GetGamepadGuid's doc comment for
    /// why this is a MODEL fingerprint, not a per-unit identity. Still useful layered with
    /// Serial and Name (see BuildIdentity): it rules out cross-model name collisions (two
    /// different pads that both call themselves "Wireless Controller", say) even where it
    /// can't distinguish two identical units of the same model.</summary>
    public string Guid { get; }

    private nint _gamepadHandle;

    public SdlControllerReader(uint instanceId)
    {
        SdlSubsystem.EnsureInitialized();
        InstanceId = instanceId;
        Name = SdlSubsystem.GetGamepadName(instanceId);
        Guid = SdlSubsystem.GetGamepadGuid(instanceId);

        unsafe
        {
            var handle = SDL3.SDL_OpenGamepad((SDL_JoystickID)instanceId);
            _gamepadHandle = (nint)handle;

            // Serial can only be read from an OPENED gamepad handle (unlike Name/Guid,
            // which work off the bare instance ID) - hence reading it here, after opening,
            // rather than alongside Name/Guid above.
            //
            // NOTE: SDL_GetGamepadSerial's native signature returns const char* (NULL when
            // no serial exists). This assumes ppy.SDL3-CS marshals that the same way it
            // already does for SDL_GetGamepadNameForID above (const char* -> string?,
            // null on a native NULL) - consistent with every other string-returning call
            // in this file. Worth a quick sanity check against whatever package version
            // ends up pinned in EverLinkHost.csproj if this doesn't compile as-is.
            if (_gamepadHandle != 0)
            {
                string? serial = SDL3.SDL_GetGamepadSerial((SDL_Gamepad*)_gamepadHandle);
                Serial = string.IsNullOrWhiteSpace(serial) ? null : serial;
            }
        }
        IsConnected = _gamepadHandle != 0;
    }

    /// <summary>Polls the current state. Call this on a timer. Returns true if still connected.</summary>
    public unsafe bool Poll()
    {
        if (_gamepadHandle == 0)
        {
            IsConnected = false;
            return false;
        }

        var gp = (SDL_Gamepad*)_gamepadHandle;

        // SDL_GamepadConnected reflects live hotplug state for an already-opened handle.
        // Wrapped defensively: an abrupt physical disconnect can theoretically leave the
        // handle in a state where even this query misbehaves, and Poll() runs on a timer -
        // we'd rather report "disconnected" than let an exception here kill the timer loop.
        bool connected;
        try
        {
            connected = SDL3.SDL_GamepadConnected(gp);
        }
        catch
        {
            IsConnected = false;
            LastState = default;
            return false;
        }

        IsConnected = connected;
        if (!IsConnected)
        {
            LastState = default;
            return false;
        }

        ushort buttons = 0;
        void SetIfPressed(SDL_GamepadButton sdlButton, ButtonBits ourBit)
        {
            if (SDL3.SDL_GetGamepadButton(gp, sdlButton)) buttons |= (ushort)ourBit;
        }

        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, ButtonBits.A); // SDL uses position names (SOUTH/EAST/etc), not A/B/X/Y - SOUTH maps to Xbox "A" position
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, ButtonBits.B);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, ButtonBits.X);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, ButtonBits.Y);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, ButtonBits.Start);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, ButtonBits.Back);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, ButtonBits.Guide); // the whole point of this migration
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, ButtonBits.LeftThumb);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, ButtonBits.RightThumb);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, ButtonBits.LeftShoulder);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, ButtonBits.RightShoulder);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, ButtonBits.DPadUp);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, ButtonBits.DPadDown);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, ButtonBits.DPadLeft);
        SetIfPressed(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, ButtonBits.DPadRight);

        // SDL trigger axes are 0..32767 (never negative), unlike XInput's 0..255 - rescale
        // to keep our existing wire protocol (byte 0-255) unchanged on the Relay side.
        short rawLt = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER);
        short rawRt = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER);
        byte lt = (byte)Math.Clamp(rawLt / 32767.0 * 255, 0, 255);
        byte rt = (byte)Math.Clamp(rawRt / 32767.0 * 255, 0, 255);

        // Stick axes ARE already -32768..32767 in SDL, matching XInput's range exactly -
        // no rescaling needed here, just a direct copy.
        short lx = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
        short ly = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
        short rx = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX);
        short ry = SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY);

        // SDL reports positive Y as down; the rest of this app (preview, remap, wire
        // protocol) uses XInput's convention of positive Y as up - see
        // GamepadPreview.xaml.cs's PositionStick, which negates Y again to draw the dot in
        // screen coordinates. We invert here so downstream code sees one consistent sign
        // convention, keeping this swap contained to one place.
        //
        // Note the InvertAxis helper rather than a plain unary negation: negating
        // short.MinValue (-32768) directly overflows a short (the mathematical result,
        // 32768, doesn't fit in -32768..32767), so that one extreme stick position needs
        // an explicit clamp to short.MaxValue instead of silently wrapping.
        LastState = new ControllerState
        {
            Buttons = buttons,
            LeftTrigger = lt,
            RightTrigger = rt,
            LeftX = lx,
            LeftY = InvertAxis(ly),
            RightX = rx,
            RightY = InvertAxis(ry),
        };

        return true;
    }

    /// <summary>Negates a signed 16-bit axis value, clamping the one value (short.MinValue)
    /// that would otherwise overflow on negation.</summary>
    private static short InvertAxis(short value) => value == short.MinValue ? short.MaxValue : (short)-value;

    /// <summary>Plays rumble on this physical controller, mirroring a rumble command the
    /// console sent to the emulated pad on the Relay side (see EverLink_Protocol.md's
    /// "Rumble" section and SerialLink's RMBL: line parsing, which calls this). Left/right
    /// are 0-255, matching the wire protocol's existing trigger scale - SDL wants 16-bit
    /// (0-65535) motor strengths, so both are scaled up the same way SdlController.Poll()
    /// scales SDL's wider trigger range down to fit the wire protocol's 0-255 on the way
    /// in; this is the mirror image, going out.
    ///
    /// durationMs is short and re-sent on every change rather than tracking an explicit
    /// "rumble off" event - Relay only reports a new RMBL: line when the level actually
    /// changes (see EverLink_Protocol.md), so a steady non-zero rumble would otherwise
    /// have no further calls to keep it alive past whatever duration was passed the first
    /// time. Re-arming a short duration on every received line (including possible
    /// identical repeats, since a game can re-issue the same rumble command) keeps it
    /// playing continuously without needing a separate keep-alive timer here.
    ///
    /// Called from SerialLink's serial-port receive callback, NOT the UI thread that owns
    /// SdlSubsystem.PumpEvents() - unlike Poll() above (whose own doc comment explains why
    /// it deliberately avoids calling PumpEvents itself), that's fine here specifically
    /// because SDL_RumbleGamepad's own documentation states it's safe to call from any
    /// thread, which axis/button reads are not documented as being to the same degree
    /// Poll() relies on.</summary>
    public unsafe void Rumble(byte left, byte right, uint durationMs = 150)
    {
        if (_gamepadHandle == 0 || !IsConnected)
        {
            return;
        }

        ushort lowFreq = (ushort)Math.Clamp(left * 257, 0, 65535); // 0-255 -> 0-65535 (255*257≈65535)
        ushort highFreq = (ushort)Math.Clamp(right * 257, 0, 65535);

        try
        {
            SDL3.SDL_RumbleGamepad((SDL_Gamepad*)_gamepadHandle, lowFreq, highFreq, durationMs);
        }
        catch
        {
            // Best-effort - a controller that doesn't support rumble, or one that's
            // dropped mid-call, shouldn't take down whatever's driving this (SerialLink's
            // receive handling).
        }
    }

    /// <summary>Builds the string AppSettings.RelayMemory persists and matches against
    /// across app restarts - see RelayMemory's doc comment for the full reasoning. Layers
    /// the strongest identity signal SDL actually gives us:
    ///   - Serial present (PS4/PS5, Switch Pro, some others): "serial:&lt;value&gt;" alone.
    ///     Unambiguously identifies this exact physical unit - Guid/Name aren't needed and
    ///     are left out so the same controller still matches if its GUID ever shifts (see
    ///     SdlSubsystem.GetGamepadGuid's doc comment - GUID can change if the OS driver
    ///     path changes, e.g. after a Windows update).
    ///   - No serial (most Xbox 360-style pads, including this project's own eventual
    ///     Relay-as-controller output): "model:&lt;guid&gt;:&lt;name&gt;". Guid narrows to the
    ///     specific vendor/product/revision, Name is layered on top mainly to keep the
    ///     persisted string human-readable in settings.json - it does NOT add any actual
    ///     disambiguating power beyond the Guid alone, since SDL assigns the same Guid to
    ///     every unit of a given model (this is the documented, unavoidable limit: two
    ///     identical pads of the same model/revision are indistinguishable to SDL, full
    ///     stop - no combination of Guid/Name/Serial fixes that when Serial is absent).
    /// </summary>
    public string BuildIdentity() =>
        Serial is { Length: > 0 } serial ? $"serial:{serial}" : $"model:{Guid}:{Name}";

    public void Dispose()
    {
        if (_gamepadHandle != 0)
        {
            unsafe
            {
                try
                {
                    SDL3.SDL_CloseGamepad((SDL_Gamepad*)_gamepadHandle);
                }
                catch
                {
                    // Device may have already disappeared (e.g. unplugged) - nothing more to clean up.
                }
            }
            _gamepadHandle = 0;
        }
    }
}
