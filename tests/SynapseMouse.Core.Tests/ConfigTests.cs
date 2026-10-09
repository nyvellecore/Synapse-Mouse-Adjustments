using SynapseMouse.Core.Config;
using SynapseMouse.Core.Input;
using SynapseMouse.Core.Models;

namespace SynapseMouse.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void DefaultDocument_HasStarterConfigs()
    {
        var doc = ConfigPresets.CreateDefaultDocument();
        Assert.Equal(new[] { "Default", "Gaming", "Minecraft PvP", "Sword PvP", "Mace PvP", "Custom" }, doc.Configs.Select(c => c.Name));
        Assert.Equal(doc.Configs[0].Id, doc.ActiveConfigId);
        Assert.Equal("Ctrl + Alt + 2", KeyNames.Format(doc.Configs[2].Hotkey));
        Assert.True(doc.MasterEnabled);
    }

    [Fact]
    public void DefaultPreset_HasNoActiveFeatures()
    {
        Assert.Empty(FeatureSummary.GetActiveFeatures(ConfigPresets.Create(ConfigPresets.Default)));
        Assert.False(ProcessorSettings.FromConfig(ConfigPresets.Create(ConfigPresets.Default), true).NeedsMouseHook);
    }

    [Fact]
    public void Document_RoundTripsExactly()
    {
        var doc = ConfigPresets.CreateDefaultDocument();
        var pvp = doc.Configs.First(c => c.Name == "Minecraft PvP");
        pvp.Debounce.TimeMs = 5;
        pvp.DoubleClick.Enabled = true;
        pvp.DoubleClick.IntervalMs = 50;
        pvp.DoubleClickRight.Enabled = true;
        pvp.DoubleClickRight.IntervalMs = 60;
        pvp.Sensitivity.XSensitivity = 0.85;
        pvp.Buttons.SetMapping(InputSource.MiddleButton, ActionType.Keyboard, new KeyChord(0x46, KeyModifiers.Shift));
        pvp.Buttons.VirtualButtons.Add(new VirtualButton { Name = "Back", Trigger = new KeyChord(KeyNames.VkF13), Action = ActionType.BackButton });
        doc.ActiveConfigId = pvp.Id;
        doc.MasterEnabled = true;
        doc.App.AppAssociations.Add(new AppAssociation { ProcessName = "javaw.exe", WindowTitleContains = "Minecraft", ConfigId = pvp.Id });
        doc.App.DoubleClickToggleHotkey = new KeyChord(0x44, KeyModifiers.Ctrl | KeyModifiers.Alt); // Ctrl+Alt+D

        string json = ConfigSerializer.Serialize(doc);
        var loaded = ConfigSerializer.DeserializeDocument(json);
        Assert.Equal(json, ConfigSerializer.Serialize(loaded));
        var restored = loaded.Configs.First(c => c.Id == loaded.ActiveConfigId);
        Assert.Equal("Minecraft PvP", restored.Name);
        Assert.Equal(5, restored.Debounce.TimeMs);
        Assert.True(restored.DoubleClick.Enabled);
        Assert.True(restored.DoubleClickRight.Enabled);
        Assert.Equal(60, restored.DoubleClickRight.IntervalMs);
        Assert.Equal(0.85, restored.Sensitivity.XSensitivity);
        Assert.Equal(ActionType.Keyboard, restored.Buttons.GetMapping(InputSource.MiddleButton).Action);
        Assert.Single(loaded.App.AppAssociations);
        Assert.Equal(new KeyChord(0x44, KeyModifiers.Ctrl | KeyModifiers.Alt), loaded.App.DoubleClickToggleHotkey);
    }

    [Fact]
    public void Sanitize_ClampsOutOfRangeValues()
    {
        var config = new MouseConfig
        {
            Name = " \u0001 ",
            Debounce = { TimeMs = 5000 },
            DoubleClick = { IntervalMs = 2, PressDurationMs = 50 },
            DoubleClickRight = { IntervalMs = 0, PressDurationMs = 200 },
            Sensitivity = { XSensitivity = double.NaN, YSensitivity = 99, PointerSpeed = 55, NativeDpi = 0, ActiveStage = 42 },
            Scroll = { LinesPerNotch = -3, Sensitivity = 0 },
        };
        ConfigSanitizer.Sanitize(config);
        Assert.Equal("Config", config.Name);
        Assert.Equal(100, config.Debounce.TimeMs);
        Assert.Equal(51, config.DoubleClick.IntervalMs); // must exceed the press duration
        Assert.Equal(MouseButtonFlags.Right, config.DoubleClickRight.Buttons); // forced to the right button
        Assert.Equal(101, config.DoubleClickRight.IntervalMs); // must exceed the (clamped) press duration
        Assert.Equal(100, config.DoubleClickRight.PressDurationMs);
        Assert.Equal(1.0, config.Sensitivity.XSensitivity);
        Assert.Equal(10.0, config.Sensitivity.YSensitivity);
        Assert.Equal(20, config.Sensitivity.PointerSpeed);
        Assert.Equal(100, config.Sensitivity.NativeDpi);
        Assert.InRange(config.Sensitivity.ActiveStage, 0, config.Sensitivity.Stages.Count - 1);
        Assert.Equal(1, config.Scroll.LinesPerNotch);
        Assert.Equal(0.1, config.Scroll.Sensitivity);
    }

    [Fact]
    public void Sanitize_DropsInvalidMappings()
    {
        var config = new MouseConfig();
        config.Buttons.Mappings.Add(new ButtonMapping { Source = InputSource.LeftButton, Action = ActionType.Keyboard }); // no key
        config.Buttons.Mappings.Add(new ButtonMapping { Source = (InputSource)77, Action = ActionType.LeftClick });
        config.Buttons.Mappings.Add(new ButtonMapping { Source = InputSource.WheelUp, Action = ActionType.SensitivityClutch });
        config.Buttons.Mappings.Add(new ButtonMapping { Source = InputSource.RightButton, Action = ActionType.MiddleClick });
        config.Buttons.Mappings.Add(new ButtonMapping { Source = InputSource.RightButton, Action = ActionType.LeftClick });
        ConfigSanitizer.Sanitize(config);
        var mapping = Assert.Single(config.Buttons.Mappings);
        Assert.Equal(ActionType.MiddleClick, mapping.Action);
    }

    [Fact]
    public void Sanitize_RejectsReservedToggleHotkey()
    {
        var doc = ConfigPresets.CreateDefaultDocument();
        doc.App.DoubleClickToggleHotkey = InputSafety.EmergencyChord.Clone();
        ConfigSanitizer.Sanitize(doc);
        Assert.Null(doc.App.DoubleClickToggleHotkey);
    }

    [Fact]
    public void ExportImport_RoundTrip_GetsNewIdentity()
    {
        var config = ConfigPresets.Create(ConfigPresets.MinecraftPvp);
        config.Hotkey = new KeyChord(0x32, KeyModifiers.Ctrl);
        config.DoubleClick.Enabled = true;
        config.DoubleClickRight.Enabled = true;
        string json = ConfigSerializer.ExportConfig(config);
        Assert.Contains("SynapseMouseAdjustments.Config", json);
        var imported = ConfigSerializer.ImportConfig(json);
        Assert.NotEqual(config.Id, imported.Id);
        Assert.Null(imported.Hotkey);
        Assert.Equal("Minecraft PvP", imported.Name);
        Assert.True(imported.DoubleClick.Enabled);
        Assert.True(imported.DoubleClickRight.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"format\":\"something else\"}")]
    [InlineData("{\"format\":42}")]
    [InlineData("{\"format\":\"SynapseMouseAdjustments.Config\",\"schemaVersion\":99,\"config\":{}}")]
    [InlineData("{\"format\":\"SynapseMouseAdjustments.Config\",\"schemaVersion\":1}")]
    [InlineData("{\"format\":\"SynapseMouseAdjustments.Config\",\"schemaVersion\":1,\"config\":{\"id\":\"not-a-guid\"}}")]
    [InlineData("{\"format\":\"SynapseMouseAdjustments.Config\",\"schemaVersion\":\"x\",\"config\":{}}")]
    [InlineData("{\"format\":\"SynapseMouseAdjustments.Settings\"}")]
    public void ImportConfig_RejectsMalformedFiles_WithFriendlyError(string json)
    {
        var ex = Assert.Throws<ConfigFormatException>(() => ConfigSerializer.ImportConfig(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void ImportConfig_SanitizesHostileValues()
    {
        const string json = """
            {
              "format": "SynapseMouseAdjustments.Config",
              "schemaVersion": 1,
              "config": {
                "name": "Evil",
                "debounce": { "enabled": true, "timeMs": -50, "mode": 9 },
                "doubleClick": { "enabled": true, "buttons": 255, "intervalMs": 999999 },
                "doubleClickRight": { "enabled": true, "buttons": 15, "intervalMs": 1, "timing": 7 },
                "sensitivity": { "xSensitivity": 1e308, "stages": [] },
                "buttons": { "virtualButtons": [ null, { "trigger": { "key": 16 }, "action": "Default" } ] }
              }
            }
            """;
        var config = ConfigSerializer.ImportConfig(json);
        Assert.Equal(1, config.Debounce.TimeMs);
        Assert.Equal(DebounceMode.Eager, config.Debounce.Mode);
        Assert.Equal(MouseButtonFlags.All, config.DoubleClick.Buttons);
        Assert.Equal(500, config.DoubleClick.IntervalMs);
        Assert.Equal(MouseButtonFlags.Right, config.DoubleClickRight.Buttons);
        Assert.Equal(DoubleClickTiming.OnPress, config.DoubleClickRight.Timing);
        Assert.Equal(10.0, config.Sensitivity.XSensitivity);
        Assert.NotEmpty(config.Sensitivity.Stages);
        var vb = Assert.Single(config.Buttons.VirtualButtons);
        Assert.Null(vb.Trigger); // a bare modifier is not a usable trigger
        Assert.Equal(ActionType.BackButton, vb.Action);
    }

    [Fact]
    public void DocumentImport_RepairsDanglingReferences()
    {
        var doc = ConfigPresets.CreateDefaultDocument();
        doc.ActiveConfigId = Guid.NewGuid();
        doc.DefaultConfigId = Guid.NewGuid();
        doc.App.AppAssociations.Add(new AppAssociation { ProcessName = "x.exe", ConfigId = Guid.NewGuid() });
        var loaded = ConfigSerializer.DeserializeDocument(ConfigSerializer.Serialize(doc));
        Assert.Equal(loaded.Configs[0].Id, loaded.DefaultConfigId);
        Assert.Equal(loaded.DefaultConfigId, loaded.ActiveConfigId);
        Assert.Empty(loaded.App.AppAssociations);
    }

    [Fact]
    public void Manager_CreateRenameDuplicateDeleteReset()
    {
        var manager = new ConfigManager(ConfigPresets.CreateDefaultDocument());
        int changes = 0;
        manager.Changed += (_, _) => changes++;
        var created = manager.Create("Gaming");
        Assert.Equal("Gaming (2)", created.Name);
        manager.Rename(created.Id, "Sword PvP");
        Assert.Equal("Sword PvP (2)", created.Name);
        var copy = manager.Duplicate(created.Id);
        Assert.Equal("Sword PvP (2) copy", copy.Name);
        Assert.NotEqual(created.Id, copy.Id);
        manager.SetActive(copy.Id);
        Assert.Same(copy, manager.Active);
        Assert.True(manager.Delete(copy.Id));
        Assert.Equal(manager.Document.DefaultConfigId, manager.Active.Id);
        var pvp = manager.FindByName("Minecraft PvP")!;
        pvp.Debounce.TimeMs = 42;
        pvp.Name = "My PvP";
        manager.Reset(pvp.Id);
        var reset = manager.Find(pvp.Id)!;
        Assert.Equal(5, reset.Debounce.TimeMs);
        Assert.Equal(50, reset.DoubleClickRight.IntervalMs); // Minecraft PvP preset value, per-button timings restored
        Assert.Equal("My PvP", reset.Name);
        Assert.True(changes >= 6);
    }

    [Fact]
    public void Manager_CannotDeleteLastConfig()
    {
        var doc = ConfigPresets.CreateDefaultDocument();
        doc.Configs.RemoveRange(1, doc.Configs.Count - 1);
        var manager = new ConfigManager(doc);
        Assert.False(manager.Delete(manager.Active.Id));
        Assert.Single(manager.Configs);
    }

    [Fact]
    public void Manager_DeleteRemovesAssociations()
    {
        var manager = new ConfigManager(ConfigPresets.CreateDefaultDocument());
        var pvp = manager.FindByName("Minecraft PvP")!;
        manager.App.AppAssociations.Add(new AppAssociation { ProcessName = "javaw.exe", ConfigId = pvp.Id });
        manager.Delete(pvp.Id);
        Assert.Empty(manager.App.AppAssociations);
    }

    [Fact]
    public void Manager_ImportAddsUniqueCopy()
    {
        var manager = new ConfigManager(ConfigPresets.CreateDefaultDocument());
        string json = manager.Export(manager.FindByName("Minecraft PvP")!.Id);
        var imported = manager.Import(json);
        Assert.Equal("Minecraft PvP (2)", imported.Name);
        Assert.Equal(7, manager.Configs.Count);
    }

    [Fact]
    public void Store_SavesAtomically_AndRecoversFromCorruption()
    {
        string dir = Path.Combine(Path.GetTempPath(), "synapse-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var first = store.Load();
            Assert.True(first.IsFirstRun);
            first.Document.MasterEnabled = false;
            store.Save(first.Document);
            first.Document.Configs[0].Debounce.TimeMs = 7;
            store.Save(first.Document); // second save creates the .bak

            var loaded = store.Load();
            Assert.False(loaded.IsFirstRun);
            Assert.Null(loaded.Warning);
            Assert.False(loaded.Document.MasterEnabled);
            Assert.Equal(7, loaded.Document.Configs[0].Debounce.TimeMs);

            File.WriteAllText(store.FilePath, "{ this is broken");
            var recovered = store.Load();
            Assert.NotNull(recovered.Warning);
            Assert.False(recovered.Document.MasterEnabled); // restored from the backup
            Assert.True(File.Exists(store.FilePath + ".corrupt"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Theory]
    [InlineData("javaw.exe", "Minecraft 1.21.4", true)]
    [InlineData("C:\\Program Files\\Java\\bin\\JAVAW.EXE", "Minecraft* 1.8.9", true)]
    [InlineData("javaw", "minecraft", true)]
    [InlineData("javaw.exe", "IntelliJ IDEA", false)]
    [InlineData("chrome.exe", "Minecraft wiki", false)]
    public void AutoSwitch_MatchesProcessAndTitle(string process, string title, bool expected)
    {
        var association = new AppAssociation { ProcessName = "javaw.exe", WindowTitleContains = "Minecraft", ConfigId = Guid.NewGuid() };
        Assert.Equal(expected, AutoSwitchLogic.Match(new[] { association }, process, title) is not null);
    }

    [Fact]
    public void AutoSwitch_ReturnsToPreviousConfig()
    {
        var app = new AppSettings { AutoSwitchEnabled = true, LastManualConfigId = Guid.NewGuid() };
        var pvp = Guid.NewGuid();
        var association = new AppAssociation { ProcessName = "javaw.exe", ConfigId = pvp };
        Assert.Equal(pvp, AutoSwitchLogic.Decide(app, app.LastManualConfigId, Guid.Empty, false, association, _ => true));
        Assert.Equal(app.LastManualConfigId, AutoSwitchLogic.Decide(app, pvp, Guid.Empty, true, null, _ => true));
        Assert.Null(AutoSwitchLogic.Decide(app, pvp, Guid.Empty, false, null, _ => true)); // manual choice: keep
        app.AutoSwitchEnabled = false;
        Assert.Null(AutoSwitchLogic.Decide(app, Guid.Empty, Guid.Empty, false, association, _ => true));
    }

    [Theory]
    [InlineData("Ctrl + Alt + 1", 0x31, KeyModifiers.Ctrl | KeyModifiers.Alt)]
    [InlineData("F13", 0x7C, KeyModifiers.None)]
    [InlineData("shift+page down", 0x22, KeyModifiers.Shift)]
    public void KeyNames_ParseAndFormat(string text, int vk, KeyModifiers modifiers)
    {
        Assert.True(KeyNames.TryParse(text, out var chord));
        Assert.Equal(vk, chord.Key);
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.True(KeyNames.TryParse(KeyNames.Format(chord), out var again));
        Assert.Equal(chord, again);
    }

    [Fact]
    public void PointerMath_StagesAndSpeed()
    {
        var s = new SensitivitySettings { NativeDpi = 800, DpiStagesEnabled = true, Stages = ConfigPresets.DefaultStages(), ActiveStage = 2 };
        Assert.Equal(2.0, PointerMath.StageMultiplier(s));
        Assert.Equal(1600, PointerMath.EffectiveDpi(s));
        Assert.Equal(3, PointerMath.NextStage(s, +1, wrap: false));
        s.ActiveStage = 3;
        Assert.Equal(3, PointerMath.NextStage(s, +1, wrap: false)); // stage 5 is disabled
        Assert.Equal(0, PointerMath.NextStage(s, +1, wrap: true));
        Assert.Equal(1.0, PointerMath.SpeedMultiplier(10));
        Assert.Equal(0.5, PointerMath.SpeedMultiplier(6));
        Assert.Equal(3.5, PointerMath.SpeedMultiplier(99));
    }

    [Fact]
    public void FeatureSummary_ListsBothButtons_AndRespectsSuspension()
    {
        var config = ConfigPresets.Create(ConfigPresets.MinecraftPvp);
        config.DoubleClick.Enabled = true;
        config.DoubleClickRight.Enabled = true;
        var active = FeatureSummary.GetActiveFeatures(config);
        Assert.Contains(active, f => f.Contains("Single → Double click (50 ms)", StringComparison.Ordinal));
        Assert.Contains(active, f => f.Contains("right button", StringComparison.OrdinalIgnoreCase));
        var suspended = FeatureSummary.GetActiveFeatures(config, doubleClickSuspended: true);
        Assert.DoesNotContain(suspended, f => f.StartsWith("Single", StringComparison.Ordinal));
        Assert.True(FeatureSummary.HasDoubleClick(config));
    }
}

public class SafetyTests
{
    [Fact]
    public void EmergencyChord_CannotBeUsedAsVirtualButtonOrHotkey()
    {
        var config = ConfigPresets.Create(ConfigPresets.Default);
        config.Hotkey = InputSafety.EmergencyChord.Clone();
        config.Buttons.VirtualButtons.Add(new VirtualButton { Trigger = InputSafety.EmergencyChord.Clone(), Action = ActionType.LeftClick });
        Assert.Empty(ProcessorSettings.FromConfig(config, true).VirtualButtons);
        ConfigSanitizer.Sanitize(config);
        Assert.Null(config.Hotkey);
        Assert.Null(config.Buttons.VirtualButtons[0].Trigger);
    }
}
