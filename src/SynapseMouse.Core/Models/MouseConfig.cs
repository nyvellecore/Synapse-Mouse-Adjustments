namespace SynapseMouse.Core.Models;

/// <summary>
/// A complete saved mouse setup ("config"). Everything the user can tune per config lives here.
/// Plain data only: validation lives in <see cref="Config.ConfigSanitizer"/>.
/// </summary>
public sealed class MouseConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    /// <summary>Preset the config was created from; "Reset" restores this preset.</summary>
    public string Preset { get; set; } = "Default";
    /// <summary>Master Enable state remembered by this config (used when Master scope is per-config).</summary>
    public bool MasterEnabled { get; set; } = true;
    /// <summary>Optional global hotkey that switches to this config.</summary>
    public KeyChord? Hotkey { get; set; }

    public ButtonSettings Buttons { get; set; } = new();
    public DebounceSettings Debounce { get; set; } = new();
    public SingleToDoubleSettings DoubleClick { get; set; } = new();
    /// <summary>"Single → Double click" for the right button, with its own timing (see <see cref="DoubleClick"/>).</summary>
    public SingleToDoubleSettings DoubleClickRight { get; set; } = new();
    public ClickResponseSettings ClickResponse { get; set; } = new();
    public SensitivitySettings Sensitivity { get; set; } = new();
    public ScrollSettings Scroll { get; set; } = new();
    public InputProcessingSettings Input { get; set; } = new();

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ButtonSettings
{
    /// <summary>One entry per remapped source. Sources without an entry keep their default behavior.</summary>
    public List<ButtonMapping> Mappings { get; set; } = new();
    /// <summary>Keyboard shortcuts that act as extra ("virtual") mouse buttons.</summary>
    public List<VirtualButton> VirtualButtons { get; set; } = new();

    public ButtonMapping GetMapping(InputSource source)
    {
        foreach (var m in Mappings)
        {
            if (m.Source == source)
            {
                return m;
            }
        }
        return new ButtonMapping { Source = source };
    }

    public void SetMapping(InputSource source, ActionType action, KeyChord? key = null)
    {
        Mappings.RemoveAll(m => m.Source == source);
        if (action != ActionType.Default)
        {
            Mappings.Add(new ButtonMapping { Source = source, Action = action, Key = key?.Clone() });
        }
    }
}

public sealed class ButtonMapping
{
    public InputSource Source { get; set; }
    public ActionType Action { get; set; } = ActionType.Default;
    /// <summary>Key produced when <see cref="Action"/> is <see cref="ActionType.Keyboard"/>.</summary>
    public KeyChord? Key { get; set; }
}

/// <summary>
/// A keyboard shortcut that behaves like an extra mouse button: holding the trigger holds the action.
/// This gives office mice without side buttons software "side buttons".
/// </summary>
public sealed class VirtualButton
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Virtual button";
    public bool Enabled { get; set; } = true;
    public KeyChord? Trigger { get; set; }
    public ActionType Action { get; set; } = ActionType.BackButton;
    public KeyChord? Key { get; set; }
}

public sealed class DebounceSettings
{
    public bool Enabled { get; set; }
    public int TimeMs { get; set; } = 5;
    public DebounceMode Mode { get; set; } = DebounceMode.Eager;
    public MouseButtonFlags Buttons { get; set; } = MouseButtonFlags.All;
}

/// <summary>"Single Click → Double Click": one physical click produces two click events.</summary>
public sealed class SingleToDoubleSettings
{
    public bool Enabled { get; set; }
    public MouseButtonFlags Buttons { get; set; } = MouseButtonFlags.Left;
    /// <summary>Time between the start of the first and the start of the second click.</summary>
    public int IntervalMs { get; set; } = 50;
    /// <summary>How long each generated click is held down.</summary>
    public int PressDurationMs { get; set; } = 10;
    public DoubleClickTiming Timing { get; set; } = DoubleClickTiming.OnPress;
}

public sealed class ClickResponseSettings
{
    /// <summary>Enables the press/release timing controls below.</summary>
    public bool Enabled { get; set; }
    public MouseButtonFlags Buttons { get; set; } = MouseButtonFlags.Left | MouseButtonFlags.Right;
    /// <summary>Delay added before a press is delivered.</summary>
    public int PressDelayMs { get; set; }
    /// <summary>Delay added before a release is delivered (extends the hold).</summary>
    public int ReleaseDelayMs { get; set; }
    /// <summary>Guarantees every click is held at least this long.</summary>
    public int MinimumHoldMs { get; set; }
    /// <summary>Drops repeated press events for a button that is already down.</summary>
    public bool SuppressDuplicateEvents { get; set; }
    /// <summary>Overrides the Windows double-click detection time.</summary>
    public bool OverrideDoubleClickTime { get; set; }
    public int DoubleClickTimeMs { get; set; } = 500;
}

public sealed class SensitivitySettings
{
    /// <summary>The mouse's real (hardware) DPI as entered by the user; used as the 1.0× reference.</summary>
    public int NativeDpi { get; set; } = 800;
    /// <summary>Enables software DPI stages (software movement scaling, not hardware DPI).</summary>
    public bool DpiStagesEnabled { get; set; }
    public List<DpiStage> Stages { get; set; } = new();
    public int ActiveStage { get; set; }
    public double XSensitivity { get; set; } = 1.0;
    public double YSensitivity { get; set; } = 1.0;
    public bool LinkXY { get; set; } = true;
    /// <summary>DPI used while a "Sensitivity clutch" button is held.</summary>
    public int ClutchDpi { get; set; } = 400;
    /// <summary>When true the Windows pointer speed/acceleration below are applied.</summary>
    public bool OverrideWindowsPointer { get; set; }
    /// <summary>Windows pointer speed, 1-20 (10 = 6/11 = 1.0×).</summary>
    public int PointerSpeed { get; set; } = 10;
    /// <summary>Windows "Enhance pointer precision" (pointer acceleration).</summary>
    public bool EnhancePointerPrecision { get; set; } = true;
}

public sealed class DpiStage
{
    public DpiStage()
    {
    }

    public DpiStage(int dpi, bool enabled = true)
    {
        Dpi = dpi;
        Enabled = enabled;
    }

    public int Dpi { get; set; } = 800;
    public bool Enabled { get; set; } = true;
}

public sealed class ScrollSettings
{
    /// <summary>When true the Windows scroll amounts below are applied.</summary>
    public bool OverrideWindowsScroll { get; set; }
    public int LinesPerNotch { get; set; } = 3;
    /// <summary>Scroll one screen per notch instead of a number of lines.</summary>
    public bool PageScroll { get; set; }
    public int HorizontalChars { get; set; } = 3;
    /// <summary>Software multiplier applied to wheel movement.</summary>
    public double Sensitivity { get; set; } = 1.0;
    /// <summary>Send fractional wheel deltas (smooth-scrolling apps) instead of whole notches.</summary>
    public bool FineGrained { get; set; }
    public bool ReverseVertical { get; set; }
    public bool ReverseHorizontal { get; set; }
    /// <summary>Holding this modifier turns the vertical wheel into horizontal scrolling.</summary>
    public HorizontalScrollModifier HorizontalModifier { get; set; } = HorizontalScrollModifier.None;
    public bool AccelerationEnabled { get; set; }
    /// <summary>0-100 %.</summary>
    public int AccelerationStrength { get; set; } = 50;
    public double AccelerationMax { get; set; } = 4.0;
}

/// <summary>Software input-processing options shown on the Polling Rate page.</summary>
public sealed class InputProcessingSettings
{
    public InputThreadPriority ThreadPriority { get; set; } = InputThreadPriority.Highest;
    /// <summary>Use a high-resolution timer for generated click timing.</summary>
    public bool PreciseTiming { get; set; } = true;
}
