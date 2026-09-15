using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EverLinkHost;

/// <summary>
/// A self-contained visual of one gamepad's live state - see GamepadPreview.xaml's doc
/// comment for why this exists as its own UserControl rather than inline Canvas markup.
/// Purely a rendering component: it has no idea whether the state it's showing is a raw
/// controller reading or a remapped one, no polling of its own, and no reference to any
/// SdlControllerReader/RemapProfile - the caller (RelayConfigureWindow) computes whatever
/// ControllerState it wants shown and calls UpdateState with it. This is what lets the same
/// control type serve as both the "Raw Input" and "Remapped Output" panels: from this
/// control's own perspective they're identical, just fed different states each tick.
/// </summary>
public partial class GamepadPreview : UserControl
{
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));

    public GamepadPreview()
    {
        InitializeComponent();
    }

    /// <summary>Renders one controller state snapshot - call this every poll tick with
    /// whatever state should currently be shown. There's no diffing against a previous
    /// state; every call fully redraws all fifteen elements, which is cheap enough at the
    /// ~20Hz polling rate this is actually called at (see RelayConfigureWindow's
    /// _pollTimer) to not be worth the added complexity of only touching what changed.</summary>
    public void UpdateState(ControllerState s)
    {
        var b = (ButtonBits)s.Buttons;
        SetPressed(BtnA, b.HasFlag(ButtonBits.A));
        SetPressed(BtnB, b.HasFlag(ButtonBits.B));
        SetPressed(BtnX, b.HasFlag(ButtonBits.X));
        SetPressed(BtnY, b.HasFlag(ButtonBits.Y));
        SetPressed(BtnBack, b.HasFlag(ButtonBits.Back));
        SetPressed(BtnStart, b.HasFlag(ButtonBits.Start));
        SetPressed(BtnGuide, b.HasFlag(ButtonBits.Guide));
        SetPressed(ShoulderL, b.HasFlag(ButtonBits.LeftShoulder));
        SetPressed(ShoulderR, b.HasFlag(ButtonBits.RightShoulder));
        SetPressed(DpadUp, b.HasFlag(ButtonBits.DPadUp));
        SetPressed(DpadDown, b.HasFlag(ButtonBits.DPadDown));
        SetPressed(DpadLeft, b.HasFlag(ButtonBits.DPadLeft));
        SetPressed(DpadRight, b.HasFlag(ButtonBits.DPadRight));
        SetPressed(StickLDot, b.HasFlag(ButtonBits.LeftThumb));
        SetPressed(StickRDot, b.HasFlag(ButtonBits.RightThumb));

        TriggerLFill.Width = s.LeftTrigger / 255.0 * 55;
        TriggerRFill.Width = s.RightTrigger / 255.0 * 55;

        PositionStick(StickLDot, s.LeftX, s.LeftY, centerLeft: 40, centerTop: 190);
        PositionStick(StickRDot, s.RightX, s.RightY, centerLeft: 180, centerTop: 190);
    }

    /// <summary>Resets to a fully-idle visual - used when there's no controller assigned,
    /// or a poll fails to return a live state (see RelayConfigureWindow.Tick's callers).</summary>
    public void SetIdle()
    {
        foreach (var shape in new Shape[] {
            BtnA, BtnB, BtnX, BtnY, BtnBack, BtnStart, BtnGuide, ShoulderL, ShoulderR,
            DpadUp, DpadDown, DpadLeft, DpadRight, StickLDot, StickRDot })
        {
            shape.Fill = IdleBrush;
        }
        TriggerLFill.Width = 0;
        TriggerRFill.Width = 0;
        Canvas.SetLeft(StickLDot, 40);
        Canvas.SetTop(StickLDot, 190);
        Canvas.SetLeft(StickRDot, 180);
        Canvas.SetTop(StickRDot, 190);
    }

    private static void SetPressed(Shape shape, bool pressed) => shape.Fill = pressed ? ActiveBrush : IdleBrush;

    private static void PositionStick(Ellipse dot, short x, short y, double centerLeft, double centerTop)
    {
        const double maxTravel = 20;
        double nx = x / 32768.0;
        double ny = -y / 32768.0;
        Canvas.SetLeft(dot, centerLeft + nx * maxTravel);
        Canvas.SetTop(dot, centerTop + ny * maxTravel);
    }
}
