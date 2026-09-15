using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EverLinkHost;

public partial class RelayConfigureWindow : Window
{
    private readonly RelayRow _relayRow;
    private readonly MainWindow _mainWindow; // for shared controller list/state, see MainWindow.ScannedControllers
    private readonly DispatcherTimer _pollTimer;

    // The controller this window is currently previewing/detecting against - tracked
    // separately from _relayRow.Relay.Controller, because opening this window
    // deliberately suspends the Relay's actual streaming assignment (see the constructor),
    // which would otherwise also break the live preview/detect features if they read
    // straight from the (now-null) Relay.Controller.
    private SdlControllerReader? _previewController;

    /// <summary>Math.Abs(short.MinValue) throws OverflowException - short.MinValue is
    /// -32768 but short.MaxValue is only 32767, so there's no valid result to return. This
    /// is exactly what a stick pushed to its full negative extreme reports, which is an
    /// entirely normal thing to do - not an edge case. Widening to int before taking the
    /// absolute value sidesteps the problem since int's range is symmetric enough to hold
    /// the result. This was the actual cause of the "moving a stick sometimes crashes the
    /// detect feature" bug - hitting exactly this one value.</summary>
    private static int SafeAbs(int value) => Math.Abs(value);

    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    // Which remap row (if any) is currently "armed" waiting for an input press. Null means
    // no detection in progress - normal preview/remap-editing behavior.
    private RemapRow? _detectingRow;
    private ControllerState? _detectBaselineState;

    public RelayConfigureWindow(RelayRow relayRow, MainWindow mainWindow)
    {
        InitializeComponent();
        _relayRow = relayRow;
        _mainWindow = mainWindow;

        Title = $"EverLink - Configure ({relayRow.DeviceLabel})";
        HeaderText.Text = relayRow.DeviceLabel;

        // Subscribed directly (not routed through MainWindow) so the dropdown refreshes
        // immediately when a controller is plugged in or removed while this window is
        // open - these are the same public static SDL events MainWindow itself uses, so
        // no extra plumbing is needed to react to the same hotplug activity it does.
        SdlSubsystem.GamepadAdded += OnGamepadHotplugChanged;
        SdlSubsystem.GamepadRemoved += OnGamepadHotplugChanged;

        // Capture and suspend BEFORE populating the dropdown - PopulateControllerDropdown
        // sets ControllerComboBox.SelectedItem, which synchronously fires
        // ControllerComboBox_SelectionChanged and would otherwise immediately re-assign
        // the controller we're about to suspend, fighting the very next line.
        _previewController = _relayRow.Relay.Controller;
        _relayRow.Relay.Link.SetController(null);

        PopulateControllerDropdown();
        RemapItemsControl.ItemsSource = _relayRow.Relay.Link.Remap?.BuildLiveRows();
        RefreshPresetList();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _pollTimer.Tick += (_, _) => Tick();
        _pollTimer.Start();

        Closed += (_, _) =>
        {
            SdlSubsystem.GamepadAdded -= OnGamepadHotplugChanged;
            SdlSubsystem.GamepadRemoved -= OnGamepadHotplugChanged;
            _pollTimer.Stop();

            // Resume streaming with whatever controller this window ends up leaving
            // assigned to _previewController (updated live by
            // ControllerComboBox_SelectionChanged as the person picks controllers) -
            // this is what actually reconnects the Relay's live stream, since it's been
            // sitting on null since the suspend above.
            _relayRow.Relay.Link.SetController(_previewController);

            // Persist whatever controller/remap state this window leaves behind, so it's
            // remembered next session (see MainWindow.SaveRelayMemory / AppSettings.RelayMemories).
            _mainWindow.SaveRelayMemory(_relayRow.Relay.Device.Mac);
        };
    }

    private void OnGamepadHotplugChanged(uint instanceId) => PopulateControllerDropdown();

    // ---------- Controller selection ----------

    private void PopulateControllerDropdown()
    {
        // Preserve the current selection across a refresh (e.g. triggered by an unrelated
        // controller being plugged in elsewhere) rather than resetting to null/first item.
        var previousSelection = (ControllerComboBox.SelectedItem as ControllerRow)?.InstanceId;

        var items = new List<ControllerRow?> { null }; // null = "no controller assigned"
        items.AddRange(_mainWindow.ControllerRows.Values.OrderBy(r => r.Name));

        ControllerComboBox.ItemsSource = items;

        var toSelect = previousSelection ?? _previewController?.InstanceId;
        ControllerComboBox.SelectedItem = toSelect is null
            ? null
            : items.FirstOrDefault(r => r?.InstanceId == toSelect);
    }

    private void ControllerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ControllerComboBox.SelectedItem as ControllerRow;
        _previewController = selected is null
            ? null
            : _mainWindow.ScannedControllers.GetValueOrDefault(selected.InstanceId);

        // Deliberately NOT calling _relayRow.Relay.Link.SetController here - streaming to
        // the ESP32 stays suspended for as long as this window is open (see constructor).
        // _previewController drives the preview/detect features locally; the Relay's
        // actual live assignment is only updated once, when this window closes.
        _relayRow.Refresh();
    }

    // ---------- Live preview + detect-mode polling ----------

    private DateTime? _detectArmedAt;
    private const int DetectTimeoutSeconds = 5;

    private void Tick()
    {
        var reader = _previewController;
        if (reader is null)
        {
            SetAllIdle();
            return;
        }

        if (!reader.Poll())
        {
            SetAllIdle();
            return;
        }

        var s = reader.LastState;
        UpdatePreviewVisuals(s);

        if (_detectingRow is not null && _detectBaselineState is { } baseline)
        {
            // Only actually finishes detection when IdentifyChangedSource finds a real,
            // above-noise-threshold input - this is the fix for the bug where any tiny
            // sensor jitter (completely normal for analog sticks resting near center)
            // would satisfy a plain "did anything change" check, silently end detection
            // with nothing assigned, and leave the button stuck showing "Press..."
            // forever since _detectingRow had already been cleared.
            var source = IdentifyChangedSource(baseline, s);
            if (source is not null)
            {
                FinishDetection(source);
            }
            else if (_detectArmedAt is { } armedAt && (DateTime.UtcNow - armedAt).TotalSeconds >= DetectTimeoutSeconds)
            {
                // Timed out with no qualifying input - revert to whatever was set before,
                // rather than leaving the row stuck waiting indefinitely.
                CancelDetection();
            }
            else
            {
                UpdateDetectCountdown();
            }
        }

        UpdateDetectButtonGlow();
    }

    private void UpdateDetectCountdown()
    {
        if (_detectingRow is null || _detectArmedAt is not { } armedAt) return;

        var remaining = Math.Max(0, DetectTimeoutSeconds - (int)(DateTime.UtcNow - armedAt).TotalSeconds);
        // Routed through RemapRow.DisplayText (a real bound property) rather than reaching
        // into the visual tree and setting a TextBlock's Text directly - see DisplayText's
        // doc comment for why doing that used to permanently break the binding.
        _detectingRow.DisplayText = $"Press... ({remaining}s)";
    }

    private void UpdatePreviewVisuals(ControllerState s)
    {
        RawPreview.UpdateState(s);

        // Remapped Output preview: what actually goes out over serial to the console,
        // computed the exact same way SerialLink.OnTick does for the real outgoing packet
        // (Remap?.Apply(raw) ?? raw) - so this panel is a true preview of the real
        // behavior, not a separate approximation of it that could drift out of sync with
        // what the console actually ends up seeing.
        var remap = _relayRow.Relay.Link.Remap;
        var remapped = remap?.Apply(s) ?? s;
        RemappedPreview.UpdateState(remapped);
    }

    private void SetAllIdle()
    {
        RawPreview.SetIdle();
        RemappedPreview.SetIdle();
    }

    private static bool StatesEqual(in ControllerState a, in ControllerState b) =>
        a.Buttons == b.Buttons && a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger &&
        a.LeftX == b.LeftX && a.LeftY == b.LeftY && a.RightX == b.RightX && a.RightY == b.RightY;

    // ---------- Click-to-detect remapping ----------

    // Below this normalized change magnitude, a detected input isn't considered a
    // deliberate press - filters out stick drift/analog noise so an idle controller
    // doesn't spuriously "detect" something. Same spirit as RemapProfile's own
    // AnalogToDigitalThreshold, but this is about noticing ANY meaningful change, not
    // converting a specific value.
    private const int NoiseThreshold = 3000; // out of a signed 16-bit axis range

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RemapRow row } button) return;

        if (_detectingRow == row)
        {
            // Clicking Detect again while already armed for this row cancels it.
            CancelDetection();
            return;
        }

        if (_detectingRow is not null) CancelDetection(); // only one row can be armed at a time

        var reader = _previewController;
        if (reader is null)
        {
            MessageBox.Show("Assign a controller first before detecting an input.");
            return;
        }

        _detectingRow = row;
        _detectBaselineState = reader.LastState;
        _detectArmedAt = DateTime.UtcNow;

        // Routed through RemapRow.DisplayText (a real bound property) rather than reaching
        // into the visual tree and setting a TextBlock's Text directly - see DisplayText's
        // doc comment for why doing that used to permanently break the binding, leaving
        // the button stuck on "Press... (Xs)" forever even after a real value was assigned.
        row.DisplayText = $"Press... ({DetectTimeoutSeconds}s)";
    }


    /// <summary>Lights up each Detect button's border with the accent color while the
    /// physical input it's currently mapped to is being actively pressed/moved - not just
    /// the one row currently armed for detection, so this also works as a general "what's
    /// live right now" indicator across the whole remap grid.</summary>
    private void UpdateDetectButtonGlow()
    {
        var reader = _previewController;
        if (reader is null) return;
        var s = reader.LastState;

        foreach (var item in RemapItemsControl.Items)
        {
            if (item is not RemapRow row) continue;
            var container = RemapItemsControl.ItemContainerGenerator.ContainerFromItem(item);
            if (container is null) continue;
            var button = FindVisualChild<Button>(container, "DetectButton");
            if (button is null) continue;

            bool isArmed = row == _detectingRow;
            bool sourceIsActive = IsSourceCurrentlyActive(row.SelectedSource, s);

            // BorderThickness is set ONCE, in XAML, and never changed here - only the
            // color toggles (accent vs. transparent). Toggling BorderThickness itself
            // between two different values (as an earlier version did) changes the
            // button's total layout size by that difference, which visibly nudges its
            // content and neighbors by a pixel every time the glow turns on/off - a
            // layout side effect, not something intentional about the glow itself.
            button.BorderBrush = (isArmed || sourceIsActive) ? ActiveBrush : Brushes.Transparent;
        }
    }

    // A stick direction only gets SUPPRESSED (kept dark despite clearing the noise floor)
    // when it's below this fraction of the OTHER axis's magnitude - i.e. this is the
    // "is this just drift on an otherwise single-direction push, or a real diagonal"
    // cutoff. Below it: "mostly Up, 10 degrees off to the side" correctly shows only Up.
    // At or above it: both directions are genuinely being pushed together (e.g. Up-Left at
    // roughly 45 degrees, or anything closer to diagonal than a slight lean) and BOTH
    // should light up, matching how a physical diagonal push actually feels to the person
    // doing it - a diagonal isn't "mostly one direction with the other suppressed", it's
    // both at once. 0.35 sits comfortably below "true diagonal" (which puts both axes at
    // roughly equal, ~100%, magnitude) while still well above ordinary stick drift/noise.
    private const double OffAxisSuppressionRatio = 0.35;

    /// <summary>Reads whether the physical input named by a remap source string is
    /// currently active - a simpler "is it live right now" bool for the UI glow, distinct
    /// from RemapProfile.Apply's fuller crossover conversion used for the actual outgoing
    /// packet.</summary>
    private static bool IsSourceCurrentlyActive(string source, in ControllerState s)
    {
        var b = (ButtonBits)s.Buttons;
        return source switch
        {
            "A" => b.HasFlag(ButtonBits.A),
            "B" => b.HasFlag(ButtonBits.B),
            "X" => b.HasFlag(ButtonBits.X),
            "Y" => b.HasFlag(ButtonBits.Y),
            "Start" => b.HasFlag(ButtonBits.Start),
            "Back" => b.HasFlag(ButtonBits.Back),
            "Guide" => b.HasFlag(ButtonBits.Guide),
            "LeftShoulder" => b.HasFlag(ButtonBits.LeftShoulder),
            "RightShoulder" => b.HasFlag(ButtonBits.RightShoulder),
            "LeftThumbClick" => b.HasFlag(ButtonBits.LeftThumb),
            "RightThumbClick" => b.HasFlag(ButtonBits.RightThumb),
            "DPadUp" => b.HasFlag(ButtonBits.DPadUp),
            "DPadDown" => b.HasFlag(ButtonBits.DPadDown),
            "DPadLeft" => b.HasFlag(ButtonBits.DPadLeft),
            "DPadRight" => b.HasFlag(ButtonBits.DPadRight),
            "LeftTrigger" => s.LeftTrigger > 30,
            "RightTrigger" => s.RightTrigger > 30,
            "LeftStickUp" => IsMeaningfulDirection(s.LeftY, s.LeftX, positive: true),
            "LeftStickDown" => IsMeaningfulDirection(s.LeftY, s.LeftX, positive: false),
            "LeftStickLeft" => IsMeaningfulDirection(s.LeftX, s.LeftY, positive: false),
            "LeftStickRight" => IsMeaningfulDirection(s.LeftX, s.LeftY, positive: true),
            "RightStickUp" => IsMeaningfulDirection(s.RightY, s.RightX, positive: true),
            "RightStickDown" => IsMeaningfulDirection(s.RightY, s.RightX, positive: false),
            "RightStickLeft" => IsMeaningfulDirection(s.RightX, s.RightY, positive: false),
            "RightStickRight" => IsMeaningfulDirection(s.RightX, s.RightY, positive: true),
            _ => false,
        };
    }

    /// <summary>True if the given axis is genuinely being pushed in the requested
    /// direction right now - not just picking up incidental drift from a push that's
    /// really aimed along the OTHER axis. Each axis is judged on its own magnitude
    /// relative to whichever axis is currently stronger:
    ///   - Straight Up (Y strong, X ~0): only Up passes - X's near-zero magnitude is far
    ///     below the suppression ratio of Y's.
    ///   - Up with a 10-degree lean (Y strong, X small): still only Up - X is small enough
    ///     relative to Y's magnitude to read as drift, not a deliberate push.
    ///   - A real Up-Left diagonal (X and Y both substantial, e.g. both near max): BOTH
    ///     pass - neither axis's magnitude is small relative to the other's, so neither
    ///     gets suppressed.
    /// </summary>
    private static bool IsMeaningfulDirection(short axisValue, short otherAxisValue, bool positive)
    {
        int magnitude = SafeAbs(axisValue);
        if (magnitude < NoiseThreshold) return false;
        if (positive ? axisValue <= 0 : axisValue >= 0) return false;

        int otherMagnitude = SafeAbs(otherAxisValue);
        if (otherMagnitude <= magnitude) return true; // this axis is the stronger (or equal) one - always counts

        // The other axis is stronger than this one - this axis only counts if it's still
        // a substantial fraction of that stronger push, rather than incidental drift off
        // of it (the "Up with a slight lean" case).
        return magnitude >= otherMagnitude * OffAxisSuppressionRatio;
    }

    private void FinishDetection(string source)
    {
        var row = _detectingRow;
        if (row is null) return;

        // SelectedSource's own setter clears any active DisplayText countdown override as
        // part of assigning the new value (see RemapRow.SelectedSource) - so DisplayText
        // reflects the newly-detected source immediately, through the same binding that
        // was showing the countdown a moment ago. No separate "restore the label" step
        // needed - unlike the old approach of writing "Press..." directly onto the
        // TextBlock's Text property, which detached the binding entirely and left the
        // button stuck on that text forever, even after SelectedSource changed underneath.
        row.SelectedSource = source; // write-through to the live RemapProfile - see RemapRow.OnLiveChange

        _detectingRow = null;
        _detectBaselineState = null;
        _detectArmedAt = null;
    }

    private void CancelDetection()
    {
        // Nothing was assigned, so SelectedSource doesn't change - explicitly clear the
        // countdown override instead so DisplayText reverts to showing the row's existing
        // (unchanged) SelectedSource rather than staying on "Press... (Xs)".
        _detectingRow?.ClearDisplayOverride();

        _detectingRow = null;
        _detectBaselineState = null;
        _detectArmedAt = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;
            var found = FindVisualChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Compares baseline vs current raw state and identifies which single
    /// physical input changed enough to count as a deliberate press - the core of
    /// "press a button, we figure out what it was" detection. Returns the source-name
    /// string RemapProfile already understands (e.g. "A", "LeftTrigger", "LeftStickUp"),
    /// or null if nothing crossed the noise threshold.</summary>
    private static string? IdentifyChangedSource(ControllerState baseline, ControllerState current)
    {
        // Buttons: any newly-pressed bit wins immediately - digital presses are unambiguous.
        var newlyPressed = (ButtonBits)(current.Buttons & (ushort)~baseline.Buttons);
        foreach (ButtonBits bit in Enum.GetValues<ButtonBits>())
        {
            if (!newlyPressed.HasFlag(bit)) continue;

            var name = bit switch
            {
                ButtonBits.A => "A", ButtonBits.B => "B", ButtonBits.X => "X", ButtonBits.Y => "Y",
                ButtonBits.Start => "Start", ButtonBits.Back => "Back", ButtonBits.Guide => "Guide",
                ButtonBits.LeftShoulder => "LeftShoulder", ButtonBits.RightShoulder => "RightShoulder",
                ButtonBits.LeftThumb => "LeftThumbClick", ButtonBits.RightThumb => "RightThumbClick",
                ButtonBits.DPadUp => "DPadUp", ButtonBits.DPadDown => "DPadDown",
                ButtonBits.DPadLeft => "DPadLeft", ButtonBits.DPadRight => "DPadRight",
                _ => null,
            };
            if (name is not null) return name;
        }

        // Analog: pick whichever single axis/trigger moved the most, if any moved enough
        // to clear the noise threshold - avoids spuriously picking up incidental stick
        // drift on an axis the person didn't actually mean to press. Stick deltas are
        // SIGNED here (not absolute magnitude) so the correct DIRECTION can be reported,
        // matching the directional targets (LeftStickUp/Down/Left/Right) rather than just
        // the axis - e.g. pushing left reports "LeftStickLeft", not just "something on
        // the X axis moved".
        int leftXDelta = current.LeftX - baseline.LeftX;
        int leftYDelta = current.LeftY - baseline.LeftY;
        int rightXDelta = current.RightX - baseline.RightX;
        int rightYDelta = current.RightY - baseline.RightY;

        var candidates = new (string Name, int Delta)[]
        {
            ("LeftTrigger", Math.Abs(current.LeftTrigger - baseline.LeftTrigger) * 128), // scaled to comparable range with 16-bit axes
            ("RightTrigger", Math.Abs(current.RightTrigger - baseline.RightTrigger) * 128),
            (leftXDelta >= 0 ? "LeftStickRight" : "LeftStickLeft", Math.Abs(leftXDelta)),
            (leftYDelta >= 0 ? "LeftStickUp" : "LeftStickDown", Math.Abs(leftYDelta)),
            (rightXDelta >= 0 ? "RightStickRight" : "RightStickLeft", Math.Abs(rightXDelta)),
            (rightYDelta >= 0 ? "RightStickUp" : "RightStickDown", Math.Abs(rightYDelta)),
        };

        var best = candidates.OrderByDescending(c => c.Delta).First();
        return best.Delta >= NoiseThreshold ? best.Name : null;
    }

    // ---------- Presets ----------

    private void RefreshPresetList()
    {
        var current = PresetNameComboBox.Text;
        PresetNameComboBox.ItemsSource = RemapPresetStore.ListPresetNames();
        PresetNameComboBox.Text = current;
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Type a name for the preset first."); return; }

        var profile = _relayRow.Relay.Link.Remap;
        if (profile is null) return;

        if (RemapPresetStore.Save(name, profile.Mapping))
            RefreshPresetList();
        else
            MessageBox.Show($"Couldn't save preset \"{name}\" - check the name doesn't contain invalid characters.");
    }

    private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var profile = _relayRow.Relay.Link.Remap;
        if (profile is null) return;

        if (!RemapPresetStore.Load(name, profile.Mapping))
        {
            MessageBox.Show($"No preset named \"{name}\" found.");
            return;
        }

        RemapItemsControl.ItemsSource = null;
        RemapItemsControl.ItemsSource = profile.BuildLiveRows();
    }

    private void ForgetPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (RemapPresetStore.Delete(name))
        {
            PresetNameComboBox.Text = "";
            RefreshPresetList();
        }
        else
        {
            MessageBox.Show($"No preset named \"{name}\" found.");
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = _relayRow.Relay.Link.Remap;
        if (profile is null) return;

        var defaults = RemapProfile.CreateDefault();
        foreach (var kv in defaults.Mapping) profile.Mapping[kv.Key] = kv.Value;

        RemapItemsControl.ItemsSource = null;
        RemapItemsControl.ItemsSource = profile.BuildLiveRows();
    }
}
