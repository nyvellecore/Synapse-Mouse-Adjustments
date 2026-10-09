using SynapseMouse.Core.Models;

namespace SynapseMouse.Core.Input;

/// <summary>A resolved remap action.</summary>
public readonly record struct ActionSpec(ActionType Type, int Vk, KeyModifiers Modifiers)
{
    public static ActionSpec Default => new(ActionType.Default, 0, KeyModifiers.None);
}

/// <summary>Per-button click timing transformation (click response + single→double).</summary>
public readonly record struct ClickTransform(
    long PressDelayUs,
    long ReleaseDelayUs,
    long MinHoldUs,
    bool Double,
    DoubleClickTiming Timing,
    long DoubleIntervalUs,
    long DoublePressUs)
{
    public static ClickTransform None => default;

    /// <summary>The original press can be passed straight to Windows.</summary>
    public bool PressPassable => PressDelayUs == 0 && !(Double && Timing == DoubleClickTiming.OnPress);

    /// <summary>The original release can be passed straight to Windows (hold-time rules permitting).</summary>
    public bool ReleasePassable => ReleaseDelayUs == 0 && !(Double && Timing == DoubleClickTiming.AfterRelease);

    public bool IsIdentity => PressDelayUs == 0 && ReleaseDelayUs == 0 && MinHoldUs == 0 && !Double;
}

public sealed record VirtualButtonSpec(Guid Id, int TriggerVk, KeyModifiers TriggerModifiers, ActionSpec Action);

/// <summary>
/// Immutable, pre-computed snapshot of everything the <see cref="InputProcessor"/> needs.
/// Built from the active <see cref="MouseConfig"/> whenever it changes, then swapped in atomically.
/// </summary>
public sealed class ProcessorSettings
{
    public const int SourceCount = 9;
    public const int ButtonCount = 5;

    public static ProcessorSettings Disabled { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>Remap action per <see cref="InputSource"/>.</summary>
    public ActionSpec[] SourceActions { get; init; } = Enumerable.Repeat(ActionSpec.Default, SourceCount).ToArray();

    public VirtualButtonSpec[] VirtualButtons { get; init; } = Array.Empty<VirtualButtonSpec>();

    /// <summary>Click transform per physical <see cref="MouseButton"/>.</summary>
    public ClickTransform[] Transforms { get; init; } = new ClickTransform[ButtonCount];

    public long DebounceUs { get; init; }
    public DebounceMode DebounceMode { get; init; }
    public MouseButtonFlags DebounceButtons { get; init; }
    public bool SuppressDuplicates { get; init; }

    public double MoveX { get; init; } = 1.0;
    public double MoveY { get; init; } = 1.0;
    public double ClutchX { get; init; } = 1.0;
    public double ClutchY { get; init; } = 1.0;
    public bool ClutchMapped { get; init; }

    public bool ReverseVertical { get; init; }
    public bool ReverseHorizontal { get; init; }
    public HorizontalScrollModifier HorizontalModifier { get; init; }
    public double ScrollSensitivity { get; init; } = 1.0;
    public bool ScrollFineGrained { get; init; }
    public bool ScrollAcceleration { get; init; }
    public int ScrollAccelerationStrength { get; init; }
    public double ScrollAccelerationMax { get; init; } = 1.0;

    /// <summary>Movement must be intercepted (software DPI/sensitivity or a clutch button).</summary>
    public bool MovementInterception =>
        Enabled && (!PointerMath.IsUnity(MoveX) || !PointerMath.IsUnity(MoveY) || ClutchMapped);

    public bool ScrollInterception =>
        ReverseVertical || ReverseHorizontal || HorizontalModifier != HorizontalScrollModifier.None
        || !PointerMath.IsUnity(ScrollSensitivity) || ScrollAcceleration;

    /// <summary>Whether a low-level mouse hook is required at all (otherwise zero overhead).</summary>
    public bool NeedsMouseHook =>
        Enabled && (SourceActions.Any(a => a.Type != ActionType.Default)
            || (DebounceUs > 0 && DebounceButtons != MouseButtonFlags.None)
            || Transforms.Any(t => !t.IsIdentity)
            || SuppressDuplicates
            || MovementInterception
            || ScrollInterception);

    public bool NeedsKeyboardHook => Enabled && VirtualButtons.Length > 0;

    public static ProcessorSettings FromConfig(MouseConfig config, bool enabled, bool doubleClickSuspended = false)
    {
        if (!enabled)
        {
            return Disabled;
        }

        var actions = Enumerable.Repeat(ActionSpec.Default, SourceCount).ToArray();
        foreach (var mapping in config.Buttons.Mappings)
        {
            int index = (int)mapping.Source;
            if (index < 0 || index >= SourceCount)
            {
                continue;
            }
            actions[index] = ToSpec(mapping.Action, mapping.Key, mapping.Source.IsWheel());
        }

        var virtualButtons = new List<VirtualButtonSpec>();
        foreach (var vb in config.Buttons.VirtualButtons)
        {
            if (!vb.Enabled || vb.Trigger is null || !vb.Trigger.IsValidTrigger || InputSafety.IsReserved(vb.Trigger))
            {
                continue;
            }
            var spec = ToSpec(vb.Action, vb.Key, false);
            if (spec.Type is ActionType.Default or ActionType.Disabled)
            {
                continue;
            }
            virtualButtons.Add(new VirtualButtonSpec(vb.Id, vb.Trigger.Key, vb.Trigger.Modifiers, spec));
        }

        var transforms = new ClickTransform[ButtonCount];
        var response = config.ClickResponse;
        var dbl = config.DoubleClick;
        var dblRight = config.DoubleClickRight;
        for (int i = 0; i < ButtonCount; i++)
        {
            var button = (MouseButton)i;
            bool responseOn = response.Enabled && response.Buttons.Has(button);
            bool rightOn = dblRight.Enabled && dblRight.Buttons.Has(button);
            bool legacyOn = dbl.Enabled && dbl.Buttons.Has(button);
            bool doubleOn = !doubleClickSuspended && (rightOn || legacyOn);
            // The dedicated right-button settings win when both would apply to the same button.
            var dblSource = rightOn ? dblRight : dbl;
            transforms[i] = new ClickTransform(
                responseOn ? response.PressDelayMs * 1000L : 0,
                responseOn ? response.ReleaseDelayMs * 1000L : 0,
                responseOn ? response.MinimumHoldMs * 1000L : 0,
                doubleOn,
                dblSource.Timing,
                dblSource.IntervalMs * 1000L,
                dblSource.PressDurationMs * 1000L);
        }

        var (moveX, moveY) = PointerMath.MovementMultipliers(config.Sensitivity);
        var (clutchX, clutchY) = PointerMath.ClutchMultipliers(config.Sensitivity);
        bool clutchMapped = actions.Any(a => a.Type == ActionType.SensitivityClutch)
            || virtualButtons.Any(v => v.Action.Type == ActionType.SensitivityClutch);

        var scroll = config.Scroll;
        return new ProcessorSettings
        {
            Enabled = true,
            SourceActions = actions,
            VirtualButtons = virtualButtons.ToArray(),
            Transforms = transforms,
            DebounceUs = config.Debounce.Enabled ? config.Debounce.TimeMs * 1000L : 0,
            DebounceMode = config.Debounce.Mode,
            DebounceButtons = config.Debounce.Buttons,
            SuppressDuplicates = response.SuppressDuplicateEvents,
            MoveX = moveX,
            MoveY = moveY,
            ClutchX = clutchX,
            ClutchY = clutchY,
            ClutchMapped = clutchMapped,
            ReverseVertical = scroll.ReverseVertical,
            ReverseHorizontal = scroll.ReverseHorizontal,
            HorizontalModifier = scroll.HorizontalModifier,
            ScrollSensitivity = scroll.Sensitivity,
            ScrollFineGrained = scroll.FineGrained,
            ScrollAcceleration = scroll.AccelerationEnabled && scroll.AccelerationStrength > 0,
            ScrollAccelerationStrength = scroll.AccelerationStrength,
            ScrollAccelerationMax = scroll.AccelerationMax,
        };
    }

    private static ActionSpec ToSpec(ActionType action, KeyChord? key, bool isWheel)
    {
        if (action == ActionType.Keyboard)
        {
            return key is { IsValid: true }
                ? new ActionSpec(ActionType.Keyboard, key.Key, key.Modifiers)
                : ActionSpec.Default;
        }
        if (isWheel && action == ActionType.SensitivityClutch)
        {
            return ActionSpec.Default;
        }
        return new ActionSpec(action, 0, KeyModifiers.None);
    }
}
