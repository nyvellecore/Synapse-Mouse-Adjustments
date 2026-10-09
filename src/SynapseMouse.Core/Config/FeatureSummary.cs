using System.Globalization;
using SynapseMouse.Core.Input;
using SynapseMouse.Core.Models;

namespace SynapseMouse.Core.Config;

/// <summary>Lists the features a config actually turns on (for the Dashboard and tray).</summary>
public static class FeatureSummary
{
    /// <summary>
    /// <paramref name="doubleClickSuspended"/> is true while the Single → Double click toggle hotkey has
    /// turned the feature off for every button (the config itself is unchanged).
    /// </summary>
    public static IReadOnlyList<string> GetActiveFeatures(MouseConfig config, bool doubleClickSuspended = false)
    {
        var list = new List<string>();
        var ci = CultureInfo.InvariantCulture;

        int remaps = config.Buttons.Mappings.Count(m => m.Action != ActionType.Default);
        if (remaps > 0)
        {
            list.Add(remaps == 1 ? "1 button remap" : $"{remaps} button remaps");
        }

        int virtualButtons = config.Buttons.VirtualButtons.Count(v => v.Enabled && v.Trigger is { IsValidTrigger: true });
        if (virtualButtons > 0)
        {
            list.Add(virtualButtons == 1 ? "1 virtual button" : $"{virtualButtons} virtual buttons");
        }

        if (config.Debounce.Enabled && config.Debounce.Buttons != MouseButtonFlags.None)
        {
            list.Add(string.Format(ci, "Debounce {0} ms", config.Debounce.TimeMs));
        }

        if (!doubleClickSuspended)
        {
            if (config.DoubleClick.Enabled && config.DoubleClick.Buttons != MouseButtonFlags.None)
            {
                list.Add(string.Format(ci, "Single → Double click ({0} ms)", config.DoubleClick.IntervalMs));
            }
            if (config.DoubleClickRight.Enabled)
            {
                list.Add(string.Format(ci, "Single → Double click, right button ({0} ms)", config.DoubleClickRight.IntervalMs));
            }
        }

        var r = config.ClickResponse;
        if (r.Enabled && r.Buttons != MouseButtonFlags.None && (r.PressDelayMs > 0 || r.ReleaseDelayMs > 0 || r.MinimumHoldMs > 0))
        {
            list.Add("Click response timing");
        }
        if (r.SuppressDuplicateEvents)
        {
            list.Add("Duplicate event filter");
        }
        if (r.OverrideDoubleClickTime)
        {
            list.Add(string.Format(ci, "Double-click time {0} ms", r.DoubleClickTimeMs));
        }

        var s = config.Sensitivity;
        if (s.DpiStagesEnabled && !PointerMath.IsUnity(PointerMath.StageMultiplier(s)))
        {
            list.Add(string.Format(ci, "Software DPI {0}", PointerMath.EffectiveDpi(s)));
        }
        if (!PointerMath.IsUnity(s.XSensitivity) || !PointerMath.IsUnity(PointerMath.EffectiveY(s)))
        {
            list.Add(string.Format(ci, "Sensitivity X {0:0.00} / Y {1:0.00}", s.XSensitivity, PointerMath.EffectiveY(s)));
        }
        if (s.OverrideWindowsPointer)
        {
            list.Add(string.Format(ci, "Pointer speed {0}/20{1}", s.PointerSpeed, s.EnhancePointerPrecision ? string.Empty : ", no acceleration"));
        }

        var sc = config.Scroll;
        if (sc.OverrideWindowsScroll)
        {
            list.Add(sc.PageScroll ? "Scroll one screen per notch" : string.Format(ci, "Scroll {0} lines per notch", sc.LinesPerNotch));
        }
        if (!PointerMath.IsUnity(sc.Sensitivity))
        {
            list.Add(string.Format(ci, "Scroll sensitivity {0:0.00}×", sc.Sensitivity));
        }
        if (sc.ReverseVertical || sc.ReverseHorizontal)
        {
            list.Add("Reverse scrolling");
        }
        if (sc.HorizontalModifier != HorizontalScrollModifier.None)
        {
            list.Add($"{sc.HorizontalModifier} + wheel → horizontal");
        }
        if (sc.AccelerationEnabled && sc.AccelerationStrength > 0)
        {
            list.Add("Scroll acceleration");
        }

        return list;
    }

    /// <summary>True when this config generates double clicks while the toggle hotkey has not paused it.</summary>
    public static bool HasDoubleClick(MouseConfig config) =>
        (config.DoubleClick.Enabled && config.DoubleClick.Buttons != MouseButtonFlags.None) || config.DoubleClickRight.Enabled;
}
