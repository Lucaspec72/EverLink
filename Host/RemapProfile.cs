using System.ComponentModel;

namespace EverLinkHost;

/// <summary>
/// The logical outputs on the emulated controller we send to the Relay - i.e. the
/// buttons/axes in the wire protocol (see EverLink_Protocol.md). Remapping changes which physical
/// input drives each of these, applied after reading the real controller and before
/// encoding the outgoing packet.
///
/// Stick axes are exposed as 4 directional targets each (Up/Down/Left/Right) rather than
/// one combined signed-axis target, Dolphin-style - this is what makes "map this button to
/// stick-up" or "detect which direction this input pushes" natural to express in the UI.
/// Each direction still ultimately contributes to ONE combined signed axis value in the
/// actual outgoing wire packet (see RemapProfile.Apply) - the split only exists at the
/// mapping/UI layer, not in the protocol itself.
/// </summary>
public enum RemapTarget
{
    A, B, X, Y, Start, Back, Guide, LeftShoulder, RightShoulder,
    LeftThumbClick, RightThumbClick,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    LeftTrigger, RightTrigger,
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
}

/// <summary>One row in the remapping UI: an output target and which physical source drives it.</summary>
public class RemapRow : INotifyPropertyChanged
{
    public RemapTarget Target { get; }
    public string TargetLabel => Target.ToString();
    public List<string> AvailableSources { get; }

    private string _selectedSource;
    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            _selectedSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSource)));
            // DisplayText tracks SelectedSource whenever nothing more specific (like an
            // active detect countdown) is overriding it - see DisplayText's own setter.
            // Cleared here (rather than left stale) so finishing a detection immediately
            // shows the newly-picked source instead of whatever countdown text was
            // showing a moment before.
            _countdownOverride = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
            // Optional immediate write-through, used by RelayConfigureWindow to edit a live
            // profile in place - SerialLink reads Mapping fresh every outgoing packet, so a
            // change here takes effect on the very next tick with no extra plumbing needed.
            // Null for callers that prefer the old "edit rows, then ApplyFromRows() once" flow.
            OnLiveChange?.Invoke(Target, value);
        }
    }

    // Set while a detect countdown ("Press... (3s)") is active for this row; null the rest
    // of the time, in which case DisplayText just shows SelectedSource. Kept as a separate
    // field (not written into SelectedSource itself) so the countdown text is purely
    // visual and never touches the actual mapping value or its write-through to the live
    // RemapProfile - only a real detected source should ever do that.
    private string? _countdownOverride;

    /// <summary>What the remap button's TextBlock should actually show right now - this
    /// is the ONLY thing the UI should ever bind to or set, specifically so a transient
    /// "Press... (Xs)" countdown can never permanently clobber the button's binding to
    /// SelectedSource the way directly setting a TextBlock's Text property used to (setting
    /// a dependency property's value directly detaches any binding that was there,
    /// permanently, until something explicitly rebinds it - WPF has no notion of
    /// "temporarily" overriding a bound value). Routing the countdown through this
    /// property instead keeps the binding itself completely untouched throughout - the
    /// countdown is just a different value flowing through the SAME binding, the same way
    /// SelectedSource itself changing is.</summary>
    public string DisplayText
    {
        get => _countdownOverride ?? _selectedSource;
        set
        {
            _countdownOverride = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    /// <summary>Clears any active countdown override, reverting DisplayText to plain
    /// SelectedSource - used when a detect attempt is cancelled/times out with nothing
    /// newly assigned (see RelayConfigureWindow.CancelDetection). FinishDetection doesn't
    /// need this separately: SelectedSource's own setter above already clears the
    /// countdown as part of assigning the new value.</summary>
    public void ClearDisplayOverride()
    {
        _countdownOverride = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
    }

    /// <summary>If set, called immediately whenever SelectedSource changes - see SelectedSource's setter.</summary>
    public Action<RemapTarget, string>? OnLiveChange { get; set; }

    public RemapRow(RemapTarget target, List<string> availableSources, string defaultSource)
    {
        Target = target;
        AvailableSources = availableSources;
        _selectedSource = defaultSource;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Holds the current remap configuration and applies it to a raw ControllerState to produce
/// the remapped state that actually gets sent over serial.
///
/// Every output target can be driven by ANY physical input - buttons, triggers, or stick
/// axes - not just ones of the matching type. Crossover between digital and analog is
/// handled by normalizing every source to a common 0..1 (buttons/triggers) or -1..1 (stick
/// axes) scale before applying it to whatever the target expects: an analog source driving
/// a digital target is thresholded (see AnalogToDigitalThreshold), a digital source driving
/// an analog target reports as fully off or fully on. See Apply() for the exact rules.
/// </summary>
public class RemapProfile
{
    // All physical inputs - buttons, triggers, and stick directions - in one combined
    // list, so any output target can be driven by any physical input regardless of type.
    // Crossover conversion (e.g. mapping a face button to a trigger, or a trigger to a
    // stick direction) happens in Apply() below: digital->analog sources report 0 or
    // full-scale, analog->digital sources are thresholded. "None" disables the output
    // entirely. Stick directions read from the SAME underlying physical axis regardless
    // of which direction is asked for - e.g. "LeftStickUp" as a SOURCE reads the physical
    // left stick's Y axis and reports its positive-Y magnitude only (0 when pushed the
    // other way or centered).
    public static readonly List<string> ButtonSources = new()
    {
        "None", "A", "B", "X", "Y", "Start", "Back", "Guide", "LeftShoulder", "RightShoulder",
        "LeftThumbClick", "RightThumbClick", "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
        "LeftTrigger", "RightTrigger",
        "LeftStickUp", "LeftStickDown", "LeftStickLeft", "LeftStickRight",
        "RightStickUp", "RightStickDown", "RightStickLeft", "RightStickRight",
    };

    public static readonly List<string> TriggerSources = ButtonSources;
    public static readonly List<string> StickAxisSources = ButtonSources;

    public Dictionary<RemapTarget, string> Mapping { get; } = new();

    public static RemapProfile CreateDefault()
    {
        var profile = new RemapProfile();
        // Identity mapping: every output driven by the same-named physical input. Target
        // enum names match source-string names exactly (e.g. RemapTarget.A -> "A",
        // RemapTarget.LeftStickUp -> "LeftStickUp"), so a plain ToString() suffices.
        foreach (RemapTarget t in Enum.GetValues<RemapTarget>())
            profile.Mapping[t] = t.ToString();
        return profile;
    }

    /// <summary>Builds the UI rows for this profile, one per output target, with the right source list per type.</summary>
    public List<RemapRow> BuildRows()
    {
        var rows = new List<RemapRow>();
        foreach (RemapTarget t in Enum.GetValues<RemapTarget>())
        {
            List<string> sources = t switch
            {
                RemapTarget.LeftTrigger or RemapTarget.RightTrigger => TriggerSources,
                RemapTarget.LeftStickUp or RemapTarget.LeftStickDown or RemapTarget.LeftStickLeft or RemapTarget.LeftStickRight
                    or RemapTarget.RightStickUp or RemapTarget.RightStickDown or RemapTarget.RightStickLeft or RemapTarget.RightStickRight
                    => StickAxisSources,
                _ => ButtonSources,
            };
            rows.Add(new RemapRow(t, sources, Mapping.TryGetValue(t, out var v) ? v : "None"));
        }
        return rows;
    }

    /// <summary>Same as BuildRows(), but each row writes changes directly back into this
    /// profile's Mapping the instant the user picks a new source - used by RelayConfigureWindow to
    /// edit a live Relay connection's profile with immediate effect, rather than requiring an
    /// explicit "apply" step. Safe to call on a profile that's actively in use by a running
    /// connection: SerialLink reads Mapping fresh on every outgoing packet (every ~4ms), so it
    /// naturally picks up whatever the dictionary currently holds.</summary>
    public List<RemapRow> BuildLiveRows()
    {
        var rows = BuildRows();
        foreach (var row in rows)
            row.OnLiveChange = (target, source) => Mapping[target] = source;
        return rows;
    }

    public void ApplyFromRows(IEnumerable<RemapRow> rows)
    {
        foreach (var row in rows) Mapping[row.Target] = row.SelectedSource;
    }

    // Below this normalized stick magnitude, an analog source mapped to a digital
    // (button) target reads as "not pressed". Chosen to ignore stick drift/noise near
    // center while still registering a clearly-intentional push in any direction.
    private const double AnalogToDigitalThreshold = 0.5;

    /// <summary>Produces the remapped state to actually send, based on the raw physical controller state.</summary>
    public ControllerState Apply(in ControllerState rawState)
    {
        // Local functions below capture this copy rather than the 'in' parameter directly -
        // C# doesn't allow ref/in/out parameters to be captured by lambdas/local functions.
        var raw = rawState;
        var outState = new ControllerState();

        // Reads ANY physical source - button, trigger, or stick axis - and normalizes it
        // to a single common scale so any source can drive any target type:
        //   buttons/triggers -> 0.0 (released/0) .. 1.0 (pressed/255)
        //   stick axes       -> -1.0 (full negative) .. 0.0 (center) .. 1.0 (full positive)
        // This is what makes crossover possible: a target doesn't care whether the source
        // was originally digital or analog, only what normalized value it reports.
        double ReadNormalized(string source) => source switch
        {
            "A" => (raw.Buttons & (ushort)ButtonBits.A) != 0 ? 1.0 : 0.0,
            "B" => (raw.Buttons & (ushort)ButtonBits.B) != 0 ? 1.0 : 0.0,
            "X" => (raw.Buttons & (ushort)ButtonBits.X) != 0 ? 1.0 : 0.0,
            "Y" => (raw.Buttons & (ushort)ButtonBits.Y) != 0 ? 1.0 : 0.0,
            "Start" => (raw.Buttons & (ushort)ButtonBits.Start) != 0 ? 1.0 : 0.0,
            "Back" => (raw.Buttons & (ushort)ButtonBits.Back) != 0 ? 1.0 : 0.0,
            "Guide" => (raw.Buttons & (ushort)ButtonBits.Guide) != 0 ? 1.0 : 0.0,
            "LeftShoulder" => (raw.Buttons & (ushort)ButtonBits.LeftShoulder) != 0 ? 1.0 : 0.0,
            "RightShoulder" => (raw.Buttons & (ushort)ButtonBits.RightShoulder) != 0 ? 1.0 : 0.0,
            "LeftThumbClick" => (raw.Buttons & (ushort)ButtonBits.LeftThumb) != 0 ? 1.0 : 0.0,
            "RightThumbClick" => (raw.Buttons & (ushort)ButtonBits.RightThumb) != 0 ? 1.0 : 0.0,
            "DPadUp" => (raw.Buttons & (ushort)ButtonBits.DPadUp) != 0 ? 1.0 : 0.0,
            "DPadDown" => (raw.Buttons & (ushort)ButtonBits.DPadDown) != 0 ? 1.0 : 0.0,
            "DPadLeft" => (raw.Buttons & (ushort)ButtonBits.DPadLeft) != 0 ? 1.0 : 0.0,
            "DPadRight" => (raw.Buttons & (ushort)ButtonBits.DPadRight) != 0 ? 1.0 : 0.0,
            "LeftTrigger" => raw.LeftTrigger / 255.0,
            "RightTrigger" => raw.RightTrigger / 255.0,
            // Directional stick sources: each reads one direction's magnitude from the
            // underlying physical axis, reporting 0 when the stick is centered or pushed
            // the OTHER way - e.g. "LeftStickUp" is 0 unless the stick is actually pushed
            // up, in which case it scales 0..1 with how far up it's pushed. This mirrors
            // how a real button behaves (0 or occupying its own directional "lane"), which
            // is what makes crossover with digital sources make sense in either direction.
            "LeftStickUp" => Math.Max(0, raw.LeftY / 32768.0),
            "LeftStickDown" => Math.Max(0, -raw.LeftY / 32768.0),
            "LeftStickLeft" => Math.Max(0, -raw.LeftX / 32768.0),
            "LeftStickRight" => Math.Max(0, raw.LeftX / 32768.0),
            "RightStickUp" => Math.Max(0, raw.RightY / 32768.0),
            "RightStickDown" => Math.Max(0, -raw.RightY / 32768.0),
            "RightStickLeft" => Math.Max(0, -raw.RightX / 32768.0),
            "RightStickRight" => Math.Max(0, raw.RightX / 32768.0),
            _ => 0.0, // "None" or unrecognized
        };

        string SourceFor(RemapTarget target) => Mapping.TryGetValue(target, out var s) ? s : "None";

        // Digital target reading any source: analog sources are thresholded (per the
        // AnalogToDigitalThreshold constant above), using absolute value so a stick pushed
        // hard in EITHER direction counts as "pressed" - a one-directional threshold would
        // silently drop half of a stick's range when mapped to a button.
        bool ReadAsDigital(RemapTarget target) => Math.Abs(ReadNormalized(SourceFor(target))) >= AnalogToDigitalThreshold;

        void SetOutputButton(RemapTarget target, ButtonBits bit)
        {
            if (ReadAsDigital(target)) outState.Buttons |= (ushort)bit;
        }

        SetOutputButton(RemapTarget.A, ButtonBits.A);
        SetOutputButton(RemapTarget.B, ButtonBits.B);
        SetOutputButton(RemapTarget.X, ButtonBits.X);
        SetOutputButton(RemapTarget.Y, ButtonBits.Y);
        SetOutputButton(RemapTarget.Start, ButtonBits.Start);
        SetOutputButton(RemapTarget.Back, ButtonBits.Back);
        SetOutputButton(RemapTarget.Guide, ButtonBits.Guide);
        SetOutputButton(RemapTarget.LeftShoulder, ButtonBits.LeftShoulder);
        SetOutputButton(RemapTarget.RightShoulder, ButtonBits.RightShoulder);
        SetOutputButton(RemapTarget.LeftThumbClick, ButtonBits.LeftThumb);
        SetOutputButton(RemapTarget.RightThumbClick, ButtonBits.RightThumb);
        SetOutputButton(RemapTarget.DPadUp, ButtonBits.DPadUp);
        SetOutputButton(RemapTarget.DPadDown, ButtonBits.DPadDown);
        SetOutputButton(RemapTarget.DPadLeft, ButtonBits.DPadLeft);
        SetOutputButton(RemapTarget.DPadRight, ButtonBits.DPadRight);

        // Trigger target reading any source: digital sources report as fully-released (0)
        // or fully-pressed (255) - there's no partial value to give a button, so it's
        // treated as a full on/off swing rather than an arbitrary partial trigger pull.
        // Analog sources (an actual trigger, or a stick axis) scale through their full
        // 0-255 range normally. A stick axis mapped to a trigger uses only its positive
        // half (negative values clamp to 0) - triggers have no negative direction to
        // represent the other half of a stick's range.
        byte ReadAsTrigger(RemapTarget target) => (byte)Math.Clamp(ReadNormalized(SourceFor(target)) * 255.0, 0, 255);

        outState.LeftTrigger = ReadAsTrigger(RemapTarget.LeftTrigger);
        outState.RightTrigger = ReadAsTrigger(RemapTarget.RightTrigger);

        // Stick axis output: combines two opposing directional TARGETS (e.g.
        // LeftStickRight and LeftStickLeft) into one signed axis value for the wire
        // protocol, which only has room for one signed value per axis - there's no
        // "directional" wire format, only X/Y. Each directional target's source is read
        // as a 0..1 magnitude (see ReadNormalized's directional cases above); the two
        // opposing magnitudes are subtracted so if both somehow read active at once
        // (e.g. two different physical buttons both mapped to opposite directions and
        // both held), they partially cancel rather than one arbitrarily winning.
        double ReadDirectionalAxis(RemapTarget positiveTarget, RemapTarget negativeTarget) =>
            ReadNormalized(SourceFor(positiveTarget)) - ReadNormalized(SourceFor(negativeTarget));

        short ToAxisValue(double normalized) => (short)Math.Clamp(normalized * 32767.0, short.MinValue, short.MaxValue);

        outState.LeftX = ToAxisValue(ReadDirectionalAxis(RemapTarget.LeftStickRight, RemapTarget.LeftStickLeft));
        outState.LeftY = ToAxisValue(ReadDirectionalAxis(RemapTarget.LeftStickUp, RemapTarget.LeftStickDown));
        outState.RightX = ToAxisValue(ReadDirectionalAxis(RemapTarget.RightStickRight, RemapTarget.RightStickLeft));
        outState.RightY = ToAxisValue(ReadDirectionalAxis(RemapTarget.RightStickUp, RemapTarget.RightStickDown));

        return outState;
    }
}
