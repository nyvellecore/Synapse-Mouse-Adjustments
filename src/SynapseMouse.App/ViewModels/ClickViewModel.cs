using System.Globalization;
using System.Windows.Threading;
using SynapseMouse.App.Services;
using SynapseMouse.Core.Config;
using SynapseMouse.Core.Models;

namespace SynapseMouse.App.ViewModels;

internal sealed class ClickViewModel : ConfigPageViewModel
{
    private readonly DispatcherTimer _statsTimer;

    public ClickViewModel(AppController controller)
        : base(controller, "click", "Click Settings", "Debounce, Single → Double click and click timing, processed in software for every app.")
    {
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => OnPropertyChanged(nameof(StatsText));

        controller.AppSettingsChanged += (sender, _) =>
        {
            if (!ReferenceEquals(sender, this))
            {
                OnPropertyChanged(nameof(DoubleClickToggleHotkey));
            }
        };
        // The toggle hotkey pauses Single → Double click without a config edit: reflect it immediately.
        controller.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DoubleClickSuspended));
            OnPropertyChanged(nameof(DoubleSummary));
        };
    }

    // ------------------------------------------------------------------ debounce

    public bool DebounceEnabled
    {
        get => Config.Debounce.Enabled;
        set => Edit(c => c.Debounce.Enabled = value);
    }

    public double DebounceMs
    {
        get => Config.Debounce.TimeMs;
        set => Edit(c => c.Debounce.TimeMs = ConfigSanitizer.DebounceMs.Clamp((int)Math.Round(value)));
    }

    public DebounceMode DebounceMode
    {
        get => Config.Debounce.Mode;
        set => Edit(c => c.Debounce.Mode = value);
    }

    public MouseButtonFlags DebounceButtons
    {
        get => Config.Debounce.Buttons;
        set => Edit(c => c.Debounce.Buttons = value);
    }

    public string DebounceSummary => Config.Debounce.Enabled
        ? string.Format(CultureInfo.CurrentCulture, "Current value: {0} ms ({1})",
            Config.Debounce.TimeMs,
            Config.Debounce.Mode == DebounceMode.Eager ? "instant press, bounce filtered" : "instant press, confirmed release")
        : "Off — every press and release from the mouse is passed on unchanged.";

    // ------------------------------------------------------------------ single → double (left button)

    public bool DoubleLeftEnabled
    {
        get => Config.DoubleClick.Enabled;
        set => Edit(c => c.DoubleClick.Enabled = value);
    }

    public double DoubleInterval
    {
        get => Config.DoubleClick.IntervalMs;
        set => Edit(c =>
        {
            c.DoubleClick.IntervalMs = (int)Math.Round(value);
            ConfigSanitizer.Sanitize(c);
        });
    }

    public double DoublePress
    {
        get => Config.DoubleClick.PressDurationMs;
        set => Edit(c =>
        {
            c.DoubleClick.PressDurationMs = (int)Math.Round(value);
            ConfigSanitizer.Sanitize(c);
        });
    }

    public DoubleClickTiming DoubleTiming
    {
        get => Config.DoubleClick.Timing;
        set => Edit(c => c.DoubleClick.Timing = value);
    }

    // ------------------------------------------------------------------ single → double (right button)

    public bool DoubleRightEnabled
    {
        get => Config.DoubleClickRight.Enabled;
        set => Edit(c => c.DoubleClickRight.Enabled = value);
    }

    public double DoubleRightInterval
    {
        get => Config.DoubleClickRight.IntervalMs;
        set => Edit(c =>
        {
            c.DoubleClickRight.IntervalMs = (int)Math.Round(value);
            ConfigSanitizer.Sanitize(c);
        });
    }

    public double DoubleRightPress
    {
        get => Config.DoubleClickRight.PressDurationMs;
        set => Edit(c =>
        {
            c.DoubleClickRight.PressDurationMs = (int)Math.Round(value);
            ConfigSanitizer.Sanitize(c);
        });
    }

    public DoubleClickTiming DoubleRightTiming
    {
        get => Config.DoubleClickRight.Timing;
        set => Edit(c => c.DoubleClickRight.Timing = value);
    }

    // ------------------------------------------------------------------ single → double (shared)

    /// <summary>True while the toggle hotkey has turned Single → Double click off for every button.</summary>
    public bool DoubleClickSuspended => Controller.DoubleClickSuspended;

    public KeyChord? DoubleClickToggleHotkey
    {
        get => Controller.App.DoubleClickToggleHotkey;
        set
        {
            var chord = ConfigSanitizer.CleanTrigger(value);
            Controller.App.DoubleClickToggleHotkey = Core.Input.InputSafety.IsReserved(chord) ? null : chord;
            Controller.OnAppSettingsEdited(this);
            OnPropertyChanged(nameof(DoubleClickToggleHotkeyStatus));
        }
    }

    public string DoubleClickToggleHotkeyStatus => Controller.Hotkeys.Failed.Contains(HotkeyService.DoubleClickToggleId)
        ? "This shortcut is already used by another app or another Synapse hotkey."
        : "Optional shortcut that turns Single → Double click on or off from anywhere — handy between PvP matches.";

    public string DoubleSummary
    {
        get
        {
            if (DoubleClickSuspended)
            {
                return "Paused with the toggle hotkey — one physical click produces one click until you press the hotkey again.";
            }
            string left = DescribeSide(Config.DoubleClick, "Left button");
            string right = DescribeSide(Config.DoubleClickRight, "Right button");
            if (left.Length == 0 && right.Length == 0)
            {
                return "Off — one physical click produces one click.";
            }
            string both = left.Length == 0 ? right : right.Length == 0 ? left : left + " " + right;
            int interval = Math.Max(EnabledInterval(Config.DoubleClick), EnabledInterval(Config.DoubleClickRight));
            string warning = interval >= WindowsSettingsDoubleClickTime
                ? " Note: an interval longer than the Windows double-click time makes Windows apps see two single clicks."
                : string.Empty;
            return both + warning;
        }
    }

    private static int EnabledInterval(SingleToDoubleSettings s) => s.Enabled ? s.IntervalMs : 0;

    private static string DescribeSide(SingleToDoubleSettings s, string button)
    {
        if (!s.Enabled)
        {
            return string.Empty;
        }
        string timing = s.Timing == DoubleClickTiming.OnPress
            ? $"click 1 at press, click 2 starts {s.IntervalMs} ms later"
            : $"click 2 starts {s.IntervalMs} ms after click 1, once you release";
        return $"{button}: one physical click → two clicks ({timing}, each held {s.PressDurationMs} ms).";
    }

    // ------------------------------------------------------------------ click response

    public bool ResponseEnabled
    {
        get => Config.ClickResponse.Enabled;
        set => Edit(c => c.ClickResponse.Enabled = value);
    }

    public MouseButtonFlags ResponseButtons
    {
        get => Config.ClickResponse.Buttons;
        set => Edit(c => c.ClickResponse.Buttons = value);
    }

    public double PressDelay
    {
        get => Config.ClickResponse.PressDelayMs;
        set => Edit(c => c.ClickResponse.PressDelayMs = ConfigSanitizer.DelayMs.Clamp((int)Math.Round(value)));
    }

    public double ReleaseDelay
    {
        get => Config.ClickResponse.ReleaseDelayMs;
        set => Edit(c => c.ClickResponse.ReleaseDelayMs = ConfigSanitizer.DelayMs.Clamp((int)Math.Round(value)));
    }

    public double MinimumHold
    {
        get => Config.ClickResponse.MinimumHoldMs;
        set => Edit(c => c.ClickResponse.MinimumHoldMs = ConfigSanitizer.MinHoldMs.Clamp((int)Math.Round(value)));
    }

    public bool SuppressDuplicates
    {
        get => Config.ClickResponse.SuppressDuplicateEvents;
        set => Edit(c => c.ClickResponse.SuppressDuplicateEvents = value);
    }

    public bool OverrideDoubleClickTime
    {
        get => Config.ClickResponse.OverrideDoubleClickTime;
        set => Edit(c => c.ClickResponse.OverrideDoubleClickTime = value);
    }

    public double DoubleClickTime
    {
        get => Config.ClickResponse.OverrideDoubleClickTime ? Config.ClickResponse.DoubleClickTimeMs : WindowsSettingsDoubleClickTime;
        set => Edit(c => c.ClickResponse.DoubleClickTimeMs = ConfigSanitizer.DoubleClickTimeMs.Clamp((int)Math.Round(value)));
    }

    public string DoubleClickTimeNote => Config.ClickResponse.OverrideDoubleClickTime
        ? "Applied to Windows while this config is active; your own setting returns when Synapse is disabled."
        : $"Using your Windows setting ({WindowsMouseSettings.ReadBaseline().DoubleClickTime} ms).";

    public string StatsText
    {
        get
        {
            var stats = Controller.Engine.GetStats();
            return string.Format(CultureInfo.CurrentCulture,
                "This session: {0} bounce events filtered · {1} duplicate presses dropped · {2} Single → Double clicks generated",
                stats.BouncesFiltered, stats.DuplicatesFiltered, stats.DoubleClicks);
        }
    }

    private int WindowsSettingsDoubleClickTime => Config.ClickResponse.OverrideDoubleClickTime
        ? Config.ClickResponse.DoubleClickTimeMs
        : WindowsMouseSettings.GetSessionDoubleClickTime();

    protected override void OnActiveChanged(bool active)
    {
        if (active)
        {
            _statsTimer.Start();
            OnPropertyChanged(nameof(StatsText));
        }
        else
        {
            _statsTimer.Stop();
        }
    }

    protected override void OnEdited()
    {
        OnPropertyChanged(nameof(DebounceSummary));
        OnPropertyChanged(nameof(DoubleSummary));
        OnPropertyChanged(nameof(DoubleInterval));
        OnPropertyChanged(nameof(DoublePress));
        OnPropertyChanged(nameof(DoubleRightInterval));
        OnPropertyChanged(nameof(DoubleRightPress));
        OnPropertyChanged(nameof(DoubleClickTime));
        OnPropertyChanged(nameof(DoubleClickTimeNote));
    }
}
