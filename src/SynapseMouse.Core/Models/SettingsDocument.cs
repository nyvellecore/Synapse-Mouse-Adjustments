namespace SynapseMouse.Core.Models;

/// <summary>
/// The root persisted document (settings.json). Also used as the full-backup format.
/// </summary>
public sealed class SettingsDocument
{
    public const string FormatId = "SynapseMouseAdjustments.Settings";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; set; } = FormatId;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Global Master Enable state (used when Master scope is global).</summary>
    public bool MasterEnabled { get; set; } = true;
    public Guid ActiveConfigId { get; set; }
    public Guid DefaultConfigId { get; set; }
    public AppSettings App { get; set; } = new();
    public List<MouseConfig> Configs { get; set; } = new();
}

/// <summary>Application-wide (not per-config) settings.</summary>
public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    /// <summary>The minimize button hides the window to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;
    public MasterStartupBehavior MasterOnStartup { get; set; } = MasterStartupBehavior.RestoreLast;
    public MasterScope MasterScope { get; set; } = MasterScope.Global;
    public bool AutoSwitchEnabled { get; set; }
    public AutoSwitchFallback AutoSwitchFallback { get; set; } = AutoSwitchFallback.ReturnToPrevious;
    public List<AppAssociation> AppAssociations { get; set; } = new();

    public KeyChord? MasterToggleHotkey { get; set; }
    public KeyChord? DpiUpHotkey { get; set; }
    public KeyChord? DpiDownHotkey { get; set; }
    public KeyChord? DpiCycleHotkey { get; set; }
    /// <summary>Hotkey that turns Single → Double click on or off for every button (e.g. between game sessions).</summary>
    public KeyChord? DoubleClickToggleHotkey { get; set; }

    /// <summary>Show a small on-screen indicator for DPI/config/master changes made via hotkeys.</summary>
    public bool ShowOsd { get; set; } = true;
    public bool ShowTrayNotifications { get; set; } = true;
    /// <summary>Launch a tiny watchdog that restores Windows mouse settings if the app is force-closed.</summary>
    public bool WatchdogEnabled { get; set; } = true;
    public bool HasShownTrayHint { get; set; }
    /// <summary>The config the user selected manually (auto-switching returns here).</summary>
    public Guid LastManualConfigId { get; set; }
}

/// <summary>Links an application to a config for automatic switching.</summary>
public sealed class AppAssociation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    /// <summary>Executable name, e.g. "javaw.exe".</summary>
    public string ProcessName { get; set; } = string.Empty;
    /// <summary>Optional case-insensitive window-title filter, e.g. "Minecraft".</summary>
    public string? WindowTitleContains { get; set; }
    public Guid ConfigId { get; set; }
}
