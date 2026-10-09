using System.Windows.Threading;
using SynapseMouse.App.Infrastructure;
using SynapseMouse.App.Native;
using SynapseMouse.Core.Config;
using SynapseMouse.Core.Input;
using SynapseMouse.Core.Models;
using SynapseMouse.Core.Persistence;

namespace SynapseMouse.App.Services;

internal enum SwitchReason
{
    Manual,
    Hotkey,
    Tray,
    Auto,
    Command,
}

/// <summary>
/// Central coordinator. Owns the settings, the input engine and every Windows service, and applies the
/// active config whenever anything changes. All public members are UI-thread only.
///
/// MASTER ENABLE is enforced here: when off, the engine gets <see cref="ProcessorSettings.Disabled"/>
/// (no hooks, everything held is released) and every Windows setting Synapse changed is restored.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessageWindow _messageWindow;
    private readonly DispatcherTimer _healthTimer;

    private NativeMethods.POINT _lastCursor;
    private uint _lastForegroundPid;
    private Guid? _lastMatchId;
    private bool _exiting;

    public AppController()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        DataDirectory = StoragePaths.ResolveDataDirectory();
        var store = new SettingsStore(DataDirectory);
        LoadResult load = store.Load();
        LoadWarning = load.Warning;
        IsFirstRun = load.IsFirstRun;

        Configs = new ConfigManager(load.Document);
        Saver = new AutoSaver(store, () => Configs.Document);
        Engine = new InputEngine();
        WindowsSettings = new WindowsMouseSettings();
        _messageWindow = new MessageWindow();
        Devices = new DeviceService(_messageWindow);
        RawInput = new RawInputMonitor(_messageWindow);
        Hotkeys = new HotkeyService(_messageWindow);
        Foreground = new ForegroundWatcher();
        Osd = new OsdService { Enabled = Configs.App.ShowOsd };
        Tray = new TrayService();
        CurrentSettings = ProcessorSettings.Disabled;

        Configs.Changed += (_, _) => Saver.Request();
        Configs.ActiveConfigChanged += (_, _) => OnActiveConfigChanged();
        Configs.ConfigListChanged += (_, _) =>
        {
            RegisterHotkeys();
            UpdateTray();
            ConfigListChanged?.Invoke(this, EventArgs.Empty);
        };
        Engine.CommandReceived += command => _dispatcher.BeginInvoke(() => OnEngineCommand(command));
        Engine.HookStateChanged += (_, _) => _dispatcher.BeginInvoke(RaiseStateChanged);
        Hotkeys.HotkeyPressed += OnHotkey;
        Foreground.ForegroundChanged += OnForegroundChanged;
        Devices.DevicesChanged += (_, _) =>
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        };
        RawInput.ActiveDeviceChanged += (_, _) =>
        {
            Devices.LastActiveHandle = RawInput.LastDevice;
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        Saver.SaveFailed += message => Tray.ShowNotification("Settings could not be saved", message);

        // Tray menu handlers are deferred so the menu has closed before windows open or the tray is disposed.
        Tray.OpenRequested += () => _dispatcher.BeginInvoke(() => ShowWindowRequested?.Invoke(null));
        Tray.SettingsRequested += () => _dispatcher.BeginInvoke(() => ShowWindowRequested?.Invoke("settings"));
        Tray.ExitRequested += () => _dispatcher.BeginInvoke(() => Exit());
        Tray.MasterToggleRequested += () => _dispatcher.BeginInvoke(() => SetMaster(!MasterEnabled));
        Tray.ConfigSelected += id => _dispatcher.BeginInvoke(() => SwitchConfig(id, SwitchReason.Tray));

        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _healthTimer.Tick += (_, _) => OnHealthTick();
    }

    // ------------------------------------------------------------------ events for view models

    /// <summary>Master Enable, engine or device status changed.</summary>
    public event EventHandler? StateChanged;
    public event EventHandler? ActiveConfigChanged;
    public event EventHandler? ConfigListChanged;
    /// <summary>A setting of the active config changed. The sender is the view model that made the edit.</summary>
    public event EventHandler? ConfigEdited;
    public event EventHandler? AppSettingsChanged;
    public event EventHandler? DevicesChanged;
    public event EventHandler? HotkeysChanged;
    public event EventHandler<ForegroundInfo>? ForegroundChanged;
    /// <summary>The main window should be shown (argument: page key or null).</summary>
    public event Action<string?>? ShowWindowRequested;

    // ------------------------------------------------------------------ services

    public ConfigManager Configs { get; }
    public AutoSaver Saver { get; }
    public InputEngine Engine { get; }
    public WindowsMouseSettings WindowsSettings { get; }
    public DeviceService Devices { get; }
    public RawInputMonitor RawInput { get; }
    public HotkeyService Hotkeys { get; }
    public ForegroundWatcher Foreground { get; }
    public OsdService Osd { get; }
    public TrayService Tray { get; }
    public string DataDirectory { get; private set; }
    public string? LoadWarning { get; }
    public bool IsFirstRun { get; }

    // ------------------------------------------------------------------ state

    public MouseConfig ActiveConfig => Configs.Active;
    public AppSettings App => Configs.App;
    public ProcessorSettings CurrentSettings { get; private set; }
    /// <summary>The current config was selected by automatic (per-application) switching.</summary>
    public bool AutoSelected { get; private set; }

    /// <summary>
    /// True while the Single → Double click toggle hotkey has turned the feature off for every button.
    /// Session state only: the saved config is unchanged, so a restart resumes the saved settings.
    /// </summary>
    public bool DoubleClickSuspended { get; private set; }

    public bool MasterEnabled =>
        App.MasterScope == MasterScope.PerConfig ? Configs.Active.MasterEnabled : Configs.Document.MasterEnabled;

    /// <summary>Windows acceleration must stay off while Synapse scales movement (it would apply twice).</summary>
    public bool AccelerationForcedOff => CurrentSettings.MovementInterception;

    public string EngineStatusText
    {
        get
        {
            if (!Engine.IsRunning)
            {
                return "Input engine stopped";
            }
            if (!MasterEnabled)
            {
                return "Idle — Master Enable is off, Windows handles the mouse normally";
            }
            if (Engine.BypassActive)
            {
                return "Paused while an administrator window is focused";
            }
            if (Engine.MouseHookActive || Engine.KeyboardHookActive)
            {
                return "Processing input in the background";
            }
            return "Active — only Windows settings are applied (no input processing needed)";
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        switch (App.MasterOnStartup)
        {
            case MasterStartupBehavior.AlwaysOn:
                SetMasterValue(true);
                break;
            case MasterStartupBehavior.AlwaysOff:
                SetMasterValue(false);
                break;
        }

        // The registry is the source of truth for "Start with Windows".
        App.StartWithWindows = StartupService.IsEnabled();
        if (App.StartWithWindows)
        {
            StartupService.RepairPathIfEnabled();
        }

        Engine.Start();
        ApplyActive();
        RegisterHotkeys();
        Foreground.WatchTitles = App.AutoSwitchEnabled;
        Foreground.Start();
        Devices.Refresh();
        if (App.WatchdogEnabled)
        {
            Watchdog.Launch();
        }
        UpdateTray();
        _healthTimer.Start();
        if (IsFirstRun)
        {
            Saver.Flush(force: true);
        }
        Log.Info($"Started. Master={(MasterEnabled ? "on" : "off")}, config='{ActiveConfig.Name}'.");
    }

    /// <summary>Fully stops the app: releases input, restores Windows settings, saves, exits.</summary>
    public void Exit() => Exit(0);

    public void Exit(int exitCode)
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        Log.Info("Exiting.");
        _healthTimer.Stop();
        Saver.Flush();
        Engine.Shutdown();
        WindowsSettings.RestoreNow();
        Dispose();
        Watchdog.SignalCleanExit();
        System.Windows.Application.Current?.Shutdown(exitCode);
    }

    /// <summary>Best-effort cleanup when the process is about to die unexpectedly.</summary>
    public void EmergencyCleanup()
    {
        try
        {
            Engine.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Emergency engine shutdown failed.", ex);
        }
        try
        {
            WindowsSettings.RestoreNow();
        }
        catch (Exception ex)
        {
            Log.Error("Emergency settings restore failed.", ex);
        }
        try
        {
            Saver.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Emergency save failed.", ex);
        }
    }

    public void Dispose()
    {
        Hotkeys.Dispose();
        Foreground.Dispose();
        Devices.Dispose();
        RawInput.Dispose();
        _messageWindow.Dispose();
        Osd.Close();
        Tray.Dispose();
        Engine.Dispose();
    }

    // ------------------------------------------------------------------ master enable

    public void SetMaster(bool enabled, bool showOsd = false)
    {
        if (MasterEnabled == enabled)
        {
            return;
        }
        SetMasterValue(enabled);
        Configs.NotifyAppSettingsEdited();
        ApplyActive();
        UpdateTray();
        if (showOsd)
        {
            Osd.Show("Master Enable", enabled ? "● ENABLED" : "○ DISABLED");
        }
        Log.Info($"Master Enable {(enabled ? "on" : "off")}.");
    }

    private void SetMasterValue(bool enabled)
    {
        if (App.MasterScope == MasterScope.PerConfig)
        {
            Configs.Active.MasterEnabled = enabled;
        }
        Configs.Document.MasterEnabled = enabled;
    }

    /// <summary>Turns Single → Double click on or off without changing the saved config (the toggle hotkey).</summary>
    public void ToggleDoubleClickSuspension(bool showOsd)
    {
        DoubleClickSuspended = !DoubleClickSuspended;
        ApplyActive();
        if (showOsd)
        {
            Osd.Show("Single → Double click", DoubleClickSuspended ? "○ OFF" : "● ON");
        }
        Log.Info($"Single → Double click {(DoubleClickSuspended ? "paused" : "resumed")} (toggle hotkey).");
    }

    // ------------------------------------------------------------------ configs

    public void SwitchConfig(Guid id, SwitchReason reason)
    {
        var target = Configs.Find(id);
        if (target is null)
        {
            return;
        }
        if (reason != SwitchReason.Auto)
        {
            App.LastManualConfigId = id;
            AutoSelected = false;
        }
        if (!Configs.SetActive(id))
        {
            Configs.NotifyAppSettingsEdited();
            return;
        }
        if (reason is SwitchReason.Hotkey or SwitchReason.Auto or SwitchReason.Command)
        {
            Osd.Show(reason == SwitchReason.Auto ? "Config (automatic)" : "Config", target.Name);
        }
        Log.Info($"Config switched to '{target.Name}' ({reason}).");
    }

    /// <summary>Call after any view model edits a setting of the active config.</summary>
    public void OnConfigEdited(object? sender)
    {
        Configs.NotifyEdited(Configs.Active);
        ApplyActive();
        ConfigEdited?.Invoke(sender, EventArgs.Empty);
    }

    /// <summary>Call after any view model edits app-wide settings.</summary>
    public void OnAppSettingsEdited(object? sender)
    {
        Configs.NotifyAppSettingsEdited();
        Osd.Enabled = App.ShowOsd;
        Foreground.WatchTitles = App.AutoSwitchEnabled;
        RegisterHotkeys();
        ApplyActive();
        UpdateTray();
        AppSettingsChanged?.Invoke(sender, EventArgs.Empty);
    }

    public void SetActiveStage(int index, object? sender, bool showOsd = false)
    {
        var s = Configs.Active.Sensitivity;
        if (index < 0 || index >= s.Stages.Count)
        {
            return;
        }
        s.ActiveStage = index;
        s.Stages[index].Enabled = true;
        OnConfigEdited(sender);
        if (showOsd)
        {
            Osd.Show($"DPI stage {index + 1}", $"{s.Stages[index].Dpi} DPI (software)");
        }
    }

    public void ChangeDpiStage(int direction, bool cycle, bool showOsd)
    {
        var s = Configs.Active.Sensitivity;
        if (!s.DpiStagesEnabled)
        {
            if (showOsd)
            {
                Osd.Show("DPI", "Software DPI stages are off");
            }
            return;
        }
        int next = cycle ? PointerMath.NextStage(s, +1, wrap: true) : PointerMath.NextStage(s, direction, wrap: false);
        SetActiveStage(next, null, showOsd);
    }

    public void ResetAll()
    {
        Log.Info("Resetting all settings.");
        Engine.Configure(ProcessorSettings.Disabled, 1.0, InputThreadPriority.Normal, false);
        try
        {
            StartupService.SetEnabled(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Could not remove the startup entry.", ex);
        }
        AutoSelected = false;
        DoubleClickSuspended = false;
        var doc = ConfigPresets.CreateDefaultDocument();
        Configs.ReplaceDocument(doc);
        if (!Watchdog.IsRunning)
        {
            Watchdog.Launch();
        }
        OnAppSettingsEdited(null);
        Saver.Flush(force: true);
    }

    public void ExportBackup(string path)
    {
        Saver.Flush();
        File.WriteAllText(path, ConfigSerializer.Serialize(Configs.Document));
    }

    /// <summary>Replaces all settings with a validated backup file. Throws <see cref="ConfigFormatException"/>.</summary>
    public void ImportBackup(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > ConfigSerializer.MaxFileBytes)
        {
            throw new ConfigFormatException("The file is too large to be a Synapse backup.");
        }
        var doc = ConfigSerializer.DeserializeDocument(File.ReadAllText(path));
        AutoSelected = false;
        Configs.ReplaceDocument(doc);
        try
        {
            StartupService.SetEnabled(doc.App.StartWithWindows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Could not update the startup entry.", ex);
        }
        if (doc.App.WatchdogEnabled && !Watchdog.IsRunning)
        {
            Watchdog.Launch();
        }
        else if (!doc.App.WatchdogEnabled)
        {
            Watchdog.Stop();
        }
        OnAppSettingsEdited(null);
        Saver.Flush(force: true);
    }

    public void ChangeStorageLocation(string directory)
    {
        Saver.Flush(force: true);
        string newDirectory = StoragePaths.ChangeDataDirectory(DataDirectory, directory);
        DataDirectory = newDirectory;
        Saver.Store = new SettingsStore(newDirectory);
        Saver.Flush(force: true);
        AppSettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ applying

    /// <summary>Pushes the active config (or "disabled") to the engine and Windows.</summary>
    public void ApplyActive()
    {
        var config = Configs.Active;
        bool enabled = MasterEnabled;
        var settings = ProcessorSettings.FromConfig(config, enabled, DoubleClickSuspended);
        var desired = enabled ? BuildDesired(config, settings) : DesiredWindowsSettings.None;
        WindowsSettings.ApplyAsync(desired);
        int speed = WindowsSettings.PredictPointerSpeed(desired);
        Engine.Configure(settings, PointerMath.SpeedMultiplier(speed), config.Input.ThreadPriority, config.Input.PreciseTiming);
        CurrentSettings = settings;
        RaiseStateChanged();
    }

    private static DesiredWindowsSettings BuildDesired(MouseConfig config, ProcessorSettings settings)
    {
        var sensitivity = config.Sensitivity;
        var scroll = config.Scroll;
        var click = config.ClickResponse;
        bool? acceleration = settings.MovementInterception
            ? false
            : sensitivity.OverrideWindowsPointer ? sensitivity.EnhancePointerPrecision : null;
        return new DesiredWindowsSettings(
            sensitivity.OverrideWindowsPointer ? sensitivity.PointerSpeed : null,
            acceleration,
            scroll.OverrideWindowsScroll ? (scroll.PageScroll ? -1 : scroll.LinesPerNotch) : null,
            scroll.OverrideWindowsScroll ? scroll.HorizontalChars : null,
            click.OverrideDoubleClickTime ? click.DoubleClickTimeMs : null);
    }

    private void OnActiveConfigChanged()
    {
        ApplyActive();
        UpdateTray();
        ActiveConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ hotkeys & commands

    public void RegisterHotkeys()
    {
        var list = new List<(string, KeyChord?)>
        {
            (HotkeyService.EmergencyId, HotkeyService.EmergencyChord),
            ("master", App.MasterToggleHotkey),
            ("dpi-up", App.DpiUpHotkey),
            ("dpi-down", App.DpiDownHotkey),
            ("dpi-cycle", App.DpiCycleHotkey),
            (HotkeyService.DoubleClickToggleId, App.DoubleClickToggleHotkey),
        };
        list.AddRange(Configs.Configs.Select(c => ("config:" + c.Id.ToString("N"), c.Hotkey)));
        Hotkeys.SetHotkeys(list);
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHotkey(string id)
    {
        switch (id)
        {
            case HotkeyService.EmergencyId:
                SetMaster(false, showOsd: true);
                return;
            case "master":
                SetMaster(!MasterEnabled, showOsd: true);
                return;
            case "dpi-up":
                if (MasterEnabled)
                {
                    ChangeDpiStage(+1, false, true);
                }
                return;
            case "dpi-down":
                if (MasterEnabled)
                {
                    ChangeDpiStage(-1, false, true);
                }
                return;
            case "dpi-cycle":
                if (MasterEnabled)
                {
                    ChangeDpiStage(+1, true, true);
                }
                return;
            case HotkeyService.DoubleClickToggleId:
                ToggleDoubleClickSuspension(showOsd: true);
                return;
        }
        if (id.StartsWith("config:", StringComparison.Ordinal) && Guid.TryParseExact(id[7..], "N", out Guid configId))
        {
            SwitchConfig(configId, SwitchReason.Hotkey);
        }
    }

    private void OnEngineCommand(ProcessorCommand command)
    {
        if (_exiting)
        {
            return;
        }
        switch (command)
        {
            case ProcessorCommand.DpiStageUp:
                ChangeDpiStage(+1, false, true);
                break;
            case ProcessorCommand.DpiStageDown:
                ChangeDpiStage(-1, false, true);
                break;
            case ProcessorCommand.DpiStageCycle:
                ChangeDpiStage(+1, true, true);
                break;
            case ProcessorCommand.NextConfig:
                SwitchConfig(Configs.Neighbor(+1).Id, SwitchReason.Command);
                break;
            case ProcessorCommand.PreviousConfig:
                SwitchConfig(Configs.Neighbor(-1).Id, SwitchReason.Command);
                break;
            case ProcessorCommand.ClutchOn:
                Osd.Show("Sensitivity clutch", $"{ActiveConfig.Sensitivity.ClutchDpi} DPI (software)");
                RaiseStateChanged();
                break;
            case ProcessorCommand.ClutchOff:
                RaiseStateChanged();
                break;
        }
    }

    // ------------------------------------------------------------------ foreground / auto switching

    private void OnForegroundChanged(object? sender, ForegroundInfo info)
    {
        Engine.Bypass = info.IsElevated;
        ForegroundChanged?.Invoke(this, info);
        if (info.IsOwnProcess)
        {
            return; // Opening Synapse to tweak a game's config must not switch away from it.
        }

        var match = AutoSwitchLogic.Match(App.AppAssociations, info.ProcessName, info.Title);
        if (info.ProcessId == _lastForegroundPid && match?.Id == _lastMatchId)
        {
            return; // Same app, same match: respect manual choices made meanwhile.
        }
        _lastForegroundPid = info.ProcessId;
        _lastMatchId = match?.Id;

        Guid? target = AutoSwitchLogic.Decide(App, Configs.Active.Id, Configs.Document.DefaultConfigId, AutoSelected, match, id => Configs.Find(id) is not null);
        if (target is { } id)
        {
            SwitchConfig(id, SwitchReason.Auto);
        }
        AutoSelected = App.AutoSwitchEnabled && match is not null && Configs.Active.Id == match.ConfigId;
    }

    // ------------------------------------------------------------------ misc

    public void UpdateTray()
    {
        if (_exiting)
        {
            return;
        }
        Tray.Update(MasterEnabled, ActiveConfig.Name, Configs.Configs.Select(c => (c.Id, c.Name)).ToList(), ActiveConfig.Id);
    }

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnHealthTick()
    {
        if (!Engine.MouseHookActive)
        {
            return;
        }
        NativeMethods.GetCursorPos(out var cursor);
        bool moved = cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y;
        _lastCursor = cursor;
        Engine.CheckHookHealth(moved);
    }
}
