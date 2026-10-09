using SynapseMouse.Core.Input;
using SynapseMouse.Core.Models;

namespace SynapseMouse.Core.Config;

/// <summary>
/// Validation limits and repair for configs. Everything loaded from disk or imported passes through
/// here, so hand-edited or malformed files can never push out-of-range values into the input engine.
/// </summary>
public static class ConfigSanitizer
{
    public const int MaxNameLength = 40;
    public const int MaxConfigs = 100;
    public const int MaxVirtualButtons = 16;
    public const int MaxAssociations = 64;
    public const int MaxStages = 5;

    public static readonly Range DebounceMs = new(1, 100);
    public static readonly Range DoubleIntervalMs = new(5, 500);
    public static readonly Range DoublePressMs = new(1, 100);
    public static readonly Range DelayMs = new(0, 100);
    public static readonly Range MinHoldMs = new(0, 200);
    public static readonly Range DoubleClickTimeMs = new(150, 1000);
    public static readonly Range NativeDpi = new(100, 32000);
    public static readonly Range StageDpi = new(50, 32000);
    public static readonly Range PointerSpeed = new(1, 20);
    public static readonly Range ScrollLines = new(1, 100);
    public static readonly Range ScrollChars = new(1, 100);
    public static readonly Range AccelStrength = new(0, 100);

    public static readonly DoubleRange Sensitivity = new(0.05, 10.0);
    public static readonly DoubleRange ScrollSensitivity = new(0.1, 10.0);
    public static readonly DoubleRange AccelMax = new(1.0, 10.0);

    /// <summary>Repairs a config in place. Returns the same instance for chaining.</summary>
    public static MouseConfig Sanitize(MouseConfig config)
    {
        if (config.Id == Guid.Empty)
        {
            config.Id = Guid.NewGuid();
        }
        config.Name = CleanName(config.Name, "Config");
        config.Preset = ConfigPresets.All.Contains(config.Preset ?? string.Empty) ? config.Preset! : ConfigPresets.Custom;
        config.Hotkey = CleanTrigger(config.Hotkey);
        if (InputSafety.IsReserved(config.Hotkey))
        {
            config.Hotkey = null;
        }

        config.Buttons ??= new ButtonSettings();
        SanitizeButtons(config.Buttons);

        config.Debounce ??= new DebounceSettings();
        var d = config.Debounce;
        d.TimeMs = DebounceMs.Clamp(d.TimeMs);
        d.Mode = Defined(d.Mode, DebounceMode.Eager);
        d.Buttons &= MouseButtonFlags.All;

        config.DoubleClick ??= new SingleToDoubleSettings();
        var dbl = config.DoubleClick;
        dbl.Buttons &= MouseButtonFlags.All;
        dbl.PressDurationMs = DoublePressMs.Clamp(dbl.PressDurationMs);
        dbl.IntervalMs = Math.Max(DoubleIntervalMs.Clamp(dbl.IntervalMs), dbl.PressDurationMs + 1);
        dbl.Timing = Defined(dbl.Timing, DoubleClickTiming.OnPress);

        config.DoubleClickRight ??= new SingleToDoubleSettings();
        var dblRight = config.DoubleClickRight;
        dblRight.Buttons = MouseButtonFlags.Right; // The right-button settings always apply to the right button.
        dblRight.PressDurationMs = DoublePressMs.Clamp(dblRight.PressDurationMs);
        dblRight.IntervalMs = Math.Max(DoubleIntervalMs.Clamp(dblRight.IntervalMs), dblRight.PressDurationMs + 1);
        dblRight.Timing = Defined(dblRight.Timing, DoubleClickTiming.OnPress);

        config.ClickResponse ??= new ClickResponseSettings();
        var r = config.ClickResponse;
        r.Buttons &= MouseButtonFlags.All;
        r.PressDelayMs = DelayMs.Clamp(r.PressDelayMs);
        r.ReleaseDelayMs = DelayMs.Clamp(r.ReleaseDelayMs);
        r.MinimumHoldMs = MinHoldMs.Clamp(r.MinimumHoldMs);
        r.DoubleClickTimeMs = DoubleClickTimeMs.Clamp(r.DoubleClickTimeMs);

        config.Sensitivity ??= new SensitivitySettings();
        SanitizeSensitivity(config.Sensitivity);

        config.Scroll ??= new ScrollSettings();
        var s = config.Scroll;
        s.LinesPerNotch = ScrollLines.Clamp(s.LinesPerNotch);
        s.HorizontalChars = ScrollChars.Clamp(s.HorizontalChars);
        s.Sensitivity = ScrollSensitivity.Clamp(s.Sensitivity);
        s.HorizontalModifier = Defined(s.HorizontalModifier, HorizontalScrollModifier.None);
        s.AccelerationStrength = AccelStrength.Clamp(s.AccelerationStrength);
        s.AccelerationMax = AccelMax.Clamp(s.AccelerationMax);

        config.Input ??= new InputProcessingSettings();
        config.Input.ThreadPriority = Defined(config.Input.ThreadPriority, InputThreadPriority.Highest);

        if (config.ModifiedUtc == default)
        {
            config.ModifiedUtc = DateTime.UtcNow;
        }
        return config;
    }

    /// <summary>Repairs a whole settings document in place (config list, references, app settings).</summary>
    public static SettingsDocument Sanitize(SettingsDocument doc)
    {
        doc.Format = SettingsDocument.FormatId;
        doc.SchemaVersion = SettingsDocument.CurrentSchemaVersion;
        doc.App ??= new AppSettings();
        doc.Configs ??= new List<MouseConfig>();
        doc.Configs.RemoveAll(c => c is null);
        if (doc.Configs.Count > MaxConfigs)
        {
            doc.Configs.RemoveRange(MaxConfigs, doc.Configs.Count - MaxConfigs);
        }

        var seenIds = new HashSet<Guid>();
        foreach (var config in doc.Configs)
        {
            Sanitize(config);
            if (!seenIds.Add(config.Id))
            {
                config.Id = Guid.NewGuid();
                seenIds.Add(config.Id);
            }
        }
        if (doc.Configs.Count == 0)
        {
            doc.Configs.Add(ConfigPresets.Create(ConfigPresets.Default));
        }

        var ids = doc.Configs.Select(c => c.Id).ToHashSet();
        if (!ids.Contains(doc.DefaultConfigId))
        {
            doc.DefaultConfigId = doc.Configs[0].Id;
        }
        if (!ids.Contains(doc.ActiveConfigId))
        {
            doc.ActiveConfigId = doc.DefaultConfigId;
        }

        var app = doc.App;
        app.MasterOnStartup = Defined(app.MasterOnStartup, MasterStartupBehavior.RestoreLast);
        app.MasterScope = Defined(app.MasterScope, MasterScope.Global);
        app.AutoSwitchFallback = Defined(app.AutoSwitchFallback, AutoSwitchFallback.ReturnToPrevious);
        app.MasterToggleHotkey = CleanTrigger(app.MasterToggleHotkey);
        app.DpiUpHotkey = CleanTrigger(app.DpiUpHotkey);
        app.DpiDownHotkey = CleanTrigger(app.DpiDownHotkey);
        app.DpiCycleHotkey = CleanTrigger(app.DpiCycleHotkey);
        app.DoubleClickToggleHotkey = CleanTrigger(app.DoubleClickToggleHotkey);
        if (InputSafety.IsReserved(app.DoubleClickToggleHotkey))
        {
            app.DoubleClickToggleHotkey = null;
        }

        if (!ids.Contains(app.LastManualConfigId))
        {
            app.LastManualConfigId = doc.ActiveConfigId;
        }

        app.AppAssociations ??= new List<AppAssociation>();
        app.AppAssociations.RemoveAll(a => a is null || !ids.Contains(a.ConfigId) || string.IsNullOrWhiteSpace(a.ProcessName));
        if (app.AppAssociations.Count > MaxAssociations)
        {
            app.AppAssociations.RemoveRange(MaxAssociations, app.AppAssociations.Count - MaxAssociations);
        }
        foreach (var a in app.AppAssociations)
        {
            if (a.Id == Guid.Empty)
            {
                a.Id = Guid.NewGuid();
            }
            a.ProcessName = Truncate(a.ProcessName.Trim(), 260);
            a.WindowTitleContains = string.IsNullOrWhiteSpace(a.WindowTitleContains)
                ? null
                : Truncate(a.WindowTitleContains.Trim(), 200);
        }
        return doc;
    }

    public static string CleanName(string? name, string fallback)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = fallback;
        }
        return Truncate(cleaned, MaxNameLength);
    }

    /// <summary>Returns a copy of a valid hotkey/trigger, or null.</summary>
    public static KeyChord? CleanTrigger(KeyChord? chord)
    {
        if (chord is null || !chord.IsValidTrigger)
        {
            return null;
        }
        return new KeyChord(chord.Key, chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win));
    }

    /// <summary>Returns a copy of a valid keyboard remap target (modifier keys allowed), or null.</summary>
    public static KeyChord? CleanTarget(KeyChord? chord)
    {
        if (chord is null || !chord.IsValid)
        {
            return null;
        }
        return new KeyChord(chord.Key, chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win));
    }

    private static void SanitizeButtons(ButtonSettings buttons)
    {
        buttons.Mappings ??= new List<ButtonMapping>();
        var cleaned = new List<ButtonMapping>();
        foreach (var m in buttons.Mappings)
        {
            if (m is null || !Enum.IsDefined(m.Source) || !Enum.IsDefined(m.Action) || m.Action == ActionType.Default)
            {
                continue;
            }
            if (cleaned.Any(c => c.Source == m.Source))
            {
                continue; // Only the first mapping per source counts.
            }
            if (m.Source.IsWheel() && m.Action == ActionType.SensitivityClutch)
            {
                continue;
            }
            m.Key = m.Action == ActionType.Keyboard ? CleanTarget(m.Key) : null;
            if (m.Action == ActionType.Keyboard && m.Key is null)
            {
                continue;
            }
            cleaned.Add(m);
        }
        buttons.Mappings = cleaned;

        buttons.VirtualButtons ??= new List<VirtualButton>();
        buttons.VirtualButtons.RemoveAll(v => v is null);
        if (buttons.VirtualButtons.Count > MaxVirtualButtons)
        {
            buttons.VirtualButtons.RemoveRange(MaxVirtualButtons, buttons.VirtualButtons.Count - MaxVirtualButtons);
        }
        foreach (var vb in buttons.VirtualButtons)
        {
            if (vb.Id == Guid.Empty)
            {
                vb.Id = Guid.NewGuid();
            }
            vb.Name = CleanName(vb.Name, "Virtual button");
            vb.Trigger = CleanTrigger(vb.Trigger);
            if (InputSafety.IsReserved(vb.Trigger))
            {
                vb.Trigger = null;
            }
            if (!Enum.IsDefined(vb.Action) || vb.Action is ActionType.Default or ActionType.Disabled)
            {
                vb.Action = ActionType.BackButton;
            }
            vb.Key = vb.Action == ActionType.Keyboard ? CleanTarget(vb.Key) : null;
        }
    }

    private static void SanitizeSensitivity(SensitivitySettings s)
    {
        s.NativeDpi = NativeDpi.Clamp(s.NativeDpi);
        s.Stages ??= new List<DpiStage>();
        s.Stages.RemoveAll(x => x is null);
        if (s.Stages.Count == 0)
        {
            s.Stages = ConfigPresets.DefaultStages();
        }
        if (s.Stages.Count > MaxStages)
        {
            s.Stages.RemoveRange(MaxStages, s.Stages.Count - MaxStages);
        }
        foreach (var stage in s.Stages)
        {
            stage.Dpi = StageDpi.Clamp(stage.Dpi);
        }
        s.ActiveStage = Math.Clamp(s.ActiveStage, 0, s.Stages.Count - 1);
        s.Stages[s.ActiveStage].Enabled = true; // The active stage can never be a disabled one.
        s.XSensitivity = Sensitivity.Clamp(s.XSensitivity);
        s.YSensitivity = Sensitivity.Clamp(s.YSensitivity);
        s.ClutchDpi = StageDpi.Clamp(s.ClutchDpi);
        s.PointerSpeed = PointerSpeed.Clamp(s.PointerSpeed);
    }

    private static T Defined<T>(T value, T fallback) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public readonly record struct Range(int Min, int Max)
    {
        public int Clamp(int value) => Math.Clamp(value, Min, Max);
    }

    public readonly record struct DoubleRange(double Min, double Max)
    {
        public double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Min <= 1 && Max >= 1 ? 1 : Min;
    }
}
