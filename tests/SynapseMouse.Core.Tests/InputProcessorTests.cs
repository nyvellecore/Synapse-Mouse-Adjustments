using SynapseMouse.Core.Config;
using SynapseMouse.Core.Input;
using SynapseMouse.Core.Models;

namespace SynapseMouse.Core.Tests;

public class InputProcessorTests
{
    private const long T0 = 10_000_000; // 10 s, in microseconds
    private const long Ms = 1_000;

    private readonly List<OutputEvent> _out = new();

    private static (InputProcessor Processor, MouseConfig Config) Create(Action<MouseConfig> configure)
    {
        var config = ConfigPresets.Create(ConfigPresets.Default);
        configure(config);
        ConfigSanitizer.Sanitize(config);
        var processor = new InputProcessor();
        processor.UpdateSettings(ProcessorSettings.FromConfig(config, enabled: true), 0, new List<OutputEvent>());
        return (processor, config);
    }

    private HookDecision Button(InputProcessor p, MouseButton b, bool down, long t, bool allowPass = true)
    {
        _out.Clear();
        return p.OnButton(b, down, t, allowPass, _out);
    }

    [Fact]
    public void DefaultConfig_DoesNotNeedHooks_AndPassesEverything()
    {
        var (p, _) = Create(_ => { });
        Assert.False(p.Settings.NeedsMouseHook);
        Assert.False(p.Settings.NeedsKeyboardHook);
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 50 * Ms));
        Assert.Empty(_out);
    }

    [Fact]
    public void DisabledProcessor_PassesEverything()
    {
        var p = new InputProcessor();
        Assert.Equal(HookDecision.Pass, p.OnButton(MouseButton.Left, true, T0, true, _out));
        Assert.Equal(HookDecision.Pass, p.OnWheel(false, 120, T0, KeyModifiers.None, true, _out));
        Assert.Equal(HookDecision.Pass, p.OnMove(5, 5, T0, _out));
        Assert.Equal(HookDecision.Pass, p.OnKey(0x41, true, T0, KeyModifiers.None, _out));
        Assert.Empty(_out);
    }

    [Fact]
    public void EagerDebounce_PassesFirstEdge_AndFiltersBounce()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 5; });
        Assert.True(p.Settings.NeedsMouseHook);
        // Press with contact bounce: D(0) U(1) D(2) … U(80)
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 1 * Ms));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0 + 2 * Ms));
        _out.Clear();
        p.Tick(T0 + 5 * Ms, _out);
        Assert.Empty(_out); // settled in the reported (down) state: nothing to do
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 80 * Ms));
        Assert.Equal(2, p.BouncesFiltered);
    }

    [Fact]
    public void EagerDebounce_FiltersPhantomClickAfterRelease()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 5; });
        Button(p, MouseButton.Left, true, T0);
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 60 * Ms));
        // Failing switch chatters right after release: D(+2) U(+3) would be a phantom double click.
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0 + 62 * Ms));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 63 * Ms));
        _out.Clear();
        p.Tick(T0 + 65 * Ms, _out);
        Assert.Empty(_out);
    }

    [Fact]
    public void EagerDebounce_ReconcilesGenuineFastRelease()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 10; });
        Button(p, MouseButton.Left, true, T0);
        // Released after only 4 ms (inside the lockout) — must not be lost.
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 4 * Ms));
        Assert.Equal(T0 + 10 * Ms, p.NextTickUs);
        _out.Clear();
        p.Tick(T0 + 10 * Ms, _out);
        var up = Assert.Single(_out);
        Assert.Equal(OutputKind.ButtonUp, up.Kind);
        Assert.Equal(MouseButton.Left, up.Button);
        Assert.Equal(long.MaxValue, p.NextTickUs);
    }

    [Fact]
    public void StableReleaseDebounce_FiltersSpuriousReleaseWhileHolding()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 8; c.Debounce.Mode = DebounceMode.StableRelease; });
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        // Drag: a 2 ms glitch release while holding must not drop the drag.
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 500 * Ms));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0 + 502 * Ms));
        _out.Clear();
        p.Tick(T0 + 520 * Ms, _out);
        Assert.Empty(_out);
        // Real release: confirmed after the stable time.
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 900 * Ms));
        _out.Clear();
        p.Tick(T0 + 905 * Ms, _out);
        Assert.Empty(_out);
        p.Tick(T0 + 908 * Ms, _out);
        var up = Assert.Single(_out);
        Assert.Equal(OutputKind.ButtonUp, up.Kind);
        Assert.Equal(1, p.BouncesFiltered);
    }

    [Fact]
    public void Debounce_OnlyAppliesToSelectedButtons()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 5; c.Debounce.Buttons = MouseButtonFlags.Left; });
        Button(p, MouseButton.Right, true, T0);
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Right, false, T0 + 1 * Ms));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Right, true, T0 + 2 * Ms));
    }

    [Fact]
    public void UnmatchedRelease_IsAlwaysPassedToWindows()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 5; c.ClickResponse.SuppressDuplicateEvents = true; });
        // Hook installed while the button was already held: first event is a release.
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0));
    }

    [Fact]
    public void SuppressDuplicates_BlocksRepeatedPress()
    {
        var (p, _) = Create(c => c.ClickResponse.SuppressDuplicateEvents = true);
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0 + 10 * Ms));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 20 * Ms));
        Assert.Equal(1, p.DuplicatesFiltered);
    }

    [Fact]
    public void Remap_RightToLeft_InjectsLeftAndBlocksOriginal()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.RightButton, ActionType.LeftClick));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0));
        Assert.Equal(OutputKind.ButtonDown, Assert.Single(_out).Kind);
        Assert.Equal(MouseButton.Left, _out[0].Button);
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, false, T0 + 40 * Ms));
        Assert.Equal(OutputKind.ButtonUp, Assert.Single(_out).Kind);
        Assert.Equal(MouseButton.Left, _out[0].Button);
    }

    [Fact]
    public void Remap_TwoInputsSameOutput_BehaveLikeOneHeldButton()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.RightButton, ActionType.LeftClick));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0)); // real left held
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0 + 5 * Ms)); // already held: nothing sent
        Assert.Empty(_out);
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, false, T0 + 10 * Ms));
        Assert.Empty(_out); // left still held
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 20 * Ms));
    }

    [Fact]
    public void Remap_ReleaseUsesActionCapturedAtPress()
    {
        var (p, config) = Create(c => c.Buttons.SetMapping(InputSource.MiddleButton, ActionType.ForwardButton));
        Button(p, MouseButton.Middle, true, T0);
        Assert.Equal(MouseButton.XButton2, _out[0].Button);
        // Config changes while the button is held.
        config.Buttons.SetMapping(InputSource.MiddleButton, ActionType.BackButton);
        p.UpdateSettings(ProcessorSettings.FromConfig(config, true), T0 + 1 * Ms, _out);
        Button(p, MouseButton.Middle, false, T0 + 30 * Ms);
        var up = Assert.Single(_out);
        Assert.Equal(OutputKind.ButtonUp, up.Kind);
        Assert.Equal(MouseButton.XButton2, up.Button);
    }

    [Fact]
    public void Remap_Disabled_SwallowsInput()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.MiddleButton, ActionType.Disabled));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Middle, true, T0));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Middle, false, T0 + 30 * Ms));
        Assert.Empty(_out);
    }

    [Fact]
    public void Remap_ToKeyboard_HoldsKeyWhileButtonHeld()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.XButton1, ActionType.Keyboard, new KeyChord(KeyNames.VkLShift)));
        Button(p, MouseButton.XButton1, true, T0);
        var down = Assert.Single(_out);
        Assert.Equal(OutputKind.KeyDown, down.Kind);
        Assert.Equal(KeyNames.VkLShift, down.Vk);
        Button(p, MouseButton.XButton1, false, T0 + 500 * Ms);
        var up = Assert.Single(_out);
        Assert.Equal(OutputKind.KeyUp, up.Kind);
        Assert.Equal(KeyNames.VkLShift, up.Vk);
    }

    [Fact]
    public void Remap_ToCommand_EmitsCommandOnPressOnly()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.MiddleButton, ActionType.DpiStageCycle));
        Button(p, MouseButton.Middle, true, T0);
        Assert.Equal(ProcessorCommand.DpiStageCycle, Assert.Single(_out).Command);
        Button(p, MouseButton.Middle, false, T0 + 30 * Ms);
        Assert.Empty(_out);
    }

    [Fact]
    public void SingleToDouble_OnPress_GeneratesTwoClicks()
    {
        var (p, _) = Create(c =>
        {
            c.DoubleClick.Enabled = true;
            c.DoubleClick.Buttons = MouseButtonFlags.Left;
            c.DoubleClick.IntervalMs = 50;
            c.DoubleClick.PressDurationMs = 10;
            c.DoubleClick.Timing = DoubleClickTiming.OnPress;
        });
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(3, _out.Count);
        Assert.Equal((OutputKind.ButtonDown, T0), (_out[0].Kind, _out[0].DueUs));
        Assert.Equal((OutputKind.ButtonUp, T0 + 10 * Ms), (_out[1].Kind, _out[1].DueUs));
        Assert.Equal((OutputKind.ButtonDown, T0 + 50 * Ms), (_out[2].Kind, _out[2].DueUs));
        // Physical release comes early (30 ms): the second click is still held at least the press duration.
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 30 * Ms));
        var up = Assert.Single(_out);
        Assert.Equal(OutputKind.ButtonUp, up.Kind);
        Assert.Equal(T0 + 60 * Ms, up.DueUs);
        Assert.Equal(1, p.GeneratedDoubleClicks);
        // Physical release long after: second click's release follows the physical release.
        Button(p, MouseButton.Left, true, T0 + 200 * Ms);
        Button(p, MouseButton.Left, false, T0 + 400 * Ms);
        Assert.Equal(T0 + 400 * Ms, Assert.Single(_out).DueUs);
    }

    [Fact]
    public void SingleToDouble_AfterRelease_GeneratesSecondClickAfterRelease()
    {
        var (p, _) = Create(c =>
        {
            c.DoubleClick.Enabled = true;
            c.DoubleClick.Buttons = MouseButtonFlags.Left;
            c.DoubleClick.IntervalMs = 50;
            c.DoubleClick.PressDurationMs = 10;
            c.DoubleClick.Timing = DoubleClickTiming.AfterRelease;
        });
        // Press passes through with zero latency.
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Empty(_out);
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 20 * Ms));
        Assert.Equal(3, _out.Count);
        Assert.Equal((OutputKind.ButtonUp, T0 + 20 * Ms), (_out[0].Kind, _out[0].DueUs));
        Assert.Equal((OutputKind.ButtonDown, T0 + 50 * Ms), (_out[1].Kind, _out[1].DueUs));
        Assert.Equal((OutputKind.ButtonUp, T0 + 60 * Ms), (_out[2].Kind, _out[2].DueUs));
    }

    [Fact]
    public void SingleToDouble_RapidClicking_NeverOverlapsEvents()
    {
        var (p, _) = Create(c =>
        {
            c.DoubleClick.Enabled = true;
            c.DoubleClick.Buttons = MouseButtonFlags.Left;
            c.DoubleClick.IntervalMs = 60;
            c.DoubleClick.PressDurationMs = 10;
        });
        var all = new List<OutputEvent>();
        long t = T0;
        for (int i = 0; i < 20; i++)
        {
            _out.Clear();
            p.OnButton(MouseButton.Left, true, t, false, _out);
            all.AddRange(_out);
            _out.Clear();
            p.OnButton(MouseButton.Left, false, t + 15 * Ms, false, _out);
            all.AddRange(_out);
            t += 45 * Ms; // Faster than the generated sequence: events must queue, never interleave.
        }
        // Output must strictly alternate down/up in time order.
        bool expectDown = true;
        long last = long.MinValue;
        foreach (var e in all)
        {
            Assert.Equal(expectDown ? OutputKind.ButtonDown : OutputKind.ButtonUp, e.Kind);
            Assert.True(e.DueUs >= last);
            last = e.DueUs;
            expectDown = !expectDown;
        }
        Assert.False(expectDown == false, "sequence must end with a release");
        Assert.Equal(80, all.Count);
    }

    [Fact]
    public void SingleToDouble_OnlyForSelectedButton()
    {
        var (p, _) = Create(c => { c.DoubleClick.Enabled = true; c.DoubleClick.Buttons = MouseButtonFlags.Right; });
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0));
        Assert.Equal(3, _out.Count);
    }

    [Fact]
    public void SingleToDouble_RightSettings_GenerateRightClicks()
    {
        var (p, _) = Create(c =>
        {
            c.DoubleClickRight.Enabled = true;
            c.DoubleClickRight.IntervalMs = 40;
            c.DoubleClickRight.PressDurationMs = 8;
        });
        // The dedicated right settings apply to the right button only; left stays normal.
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0));
        Assert.Equal(3, _out.Count);
        Assert.Equal((OutputKind.ButtonDown, T0), (_out[0].Kind, _out[0].DueUs));
        Assert.Equal((OutputKind.ButtonUp, T0 + 8 * Ms), (_out[1].Kind, _out[1].DueUs));
        Assert.Equal((OutputKind.ButtonDown, T0 + 40 * Ms), (_out[2].Kind, _out[2].DueUs));
        Assert.Equal(MouseButton.Right, _out[0].Button);
    }

    [Fact]
    public void SingleToDouble_RightSettings_OverrideLegacyOnTheSameButton()
    {
        // Legacy left settings with Buttons=Right must lose against the dedicated right instance.
        var (p, _) = Create(c =>
        {
            c.DoubleClick.Enabled = true;
            c.DoubleClick.Buttons = MouseButtonFlags.Right;
            c.DoubleClick.IntervalMs = 100;
            c.DoubleClickRight.Enabled = true;
            c.DoubleClickRight.IntervalMs = 35;
        });
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0));
        Assert.Equal((OutputKind.ButtonDown, T0 + 35 * Ms), (_out[2].Kind, _out[2].DueUs));
    }

    [Fact]
    public void SingleToDouble_Suspension_PassesEverythingThrough()
    {
        var config = ConfigPresets.Create(ConfigPresets.Default);
        config.DoubleClick.Enabled = true;
        config.DoubleClickRight.Enabled = true;
        ConfigSanitizer.Sanitize(config);
        var p = new InputProcessor();
        p.UpdateSettings(ProcessorSettings.FromConfig(config, enabled: true, doubleClickSuspended: true), T0, new List<OutputEvent>());
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Right, true, T0));
        Assert.Empty(_out);
        Assert.False(p.Settings.Transforms[(int)MouseButton.Left].Double);
        Assert.False(p.Settings.Transforms[(int)MouseButton.Right].Double);
        // ...and the config itself was not modified:
        Assert.True(config.DoubleClick.Enabled);
        Assert.True(config.DoubleClickRight.Enabled);
    }

    [Fact]
    public void ClickResponse_MinimumHold_ExtendsShortClicks()
    {
        var (p, _) = Create(c =>
        {
            c.ClickResponse.Enabled = true;
            c.ClickResponse.Buttons = MouseButtonFlags.Left;
            c.ClickResponse.MinimumHoldMs = 40;
        });
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0)); // press is still instant
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 10 * Ms));
        Assert.Equal(T0 + 40 * Ms, Assert.Single(_out).DueUs);
        // Long clicks are untouched.
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, true, T0 + 100 * Ms));
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Left, false, T0 + 200 * Ms));
    }

    [Fact]
    public void ClickResponse_PressAndReleaseDelay()
    {
        var (p, _) = Create(c =>
        {
            c.ClickResponse.Enabled = true;
            c.ClickResponse.PressDelayMs = 5;
            c.ClickResponse.ReleaseDelayMs = 7;
        });
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, true, T0));
        Assert.Equal((OutputKind.ButtonDown, T0 + 5 * Ms), (_out[0].Kind, _out[0].DueUs));
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Right, false, T0 + 50 * Ms));
        Assert.Equal((OutputKind.ButtonUp, T0 + 57 * Ms), (_out[0].Kind, _out[0].DueUs));
    }

    [Fact]
    public void QueueBusy_ForcesInjectionInsteadOfPassThrough()
    {
        var (p, _) = Create(c => { c.Debounce.Enabled = true; c.Debounce.TimeMs = 1; });
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, true, T0, allowPass: false));
        Assert.Equal(OutputKind.ButtonDown, Assert.Single(_out).Kind);
        Assert.Equal(HookDecision.Block, Button(p, MouseButton.Left, false, T0 + 30 * Ms, allowPass: false));
        Assert.Equal(OutputKind.ButtonUp, Assert.Single(_out).Kind);
    }

    [Fact]
    public void ReleaseAll_ReleasesEverythingHeld_WhenDisabled()
    {
        var (p, _) = Create(c =>
        {
            c.Buttons.SetMapping(InputSource.RightButton, ActionType.Keyboard, new KeyChord(0x46)); // F
            c.Buttons.SetMapping(InputSource.MiddleButton, ActionType.SensitivityClutch);
        });
        Button(p, MouseButton.Right, true, T0);
        Button(p, MouseButton.Middle, true, T0);
        Assert.True(p.ClutchActive);
        _out.Clear();
        p.UpdateSettings(ProcessorSettings.Disabled, T0 + 5 * Ms, _out);
        Assert.Contains(_out, e => e.Kind == OutputKind.KeyUp && e.Vk == 0x46);
        Assert.Contains(_out, e => e.Command == ProcessorCommand.ClutchOff);
        Assert.False(p.ClutchActive);
        // After disabling, the physical releases pass straight through.
        Assert.Equal(HookDecision.Pass, Button(p, MouseButton.Right, false, T0 + 10 * Ms));
    }

    [Fact]
    public void Wheel_Reverse_And_Sensitivity()
    {
        var (p, _) = Create(c => { c.Scroll.ReverseVertical = true; });
        _out.Clear();
        Assert.Equal(HookDecision.Block, p.OnWheel(false, 120, T0, KeyModifiers.None, true, _out));
        Assert.Equal(-120, Assert.Single(_out).X);

        (p, _) = Create(c => c.Scroll.Sensitivity = 0.5);
        _out.Clear();
        p.OnWheel(false, 120, T0, KeyModifiers.None, true, _out);
        Assert.Empty(_out); // half a notch accumulated
        p.OnWheel(false, 120, T0 + 200 * Ms, KeyModifiers.None, true, _out);
        Assert.Equal(120, Assert.Single(_out).X);

        (p, _) = Create(c => { c.Scroll.Sensitivity = 0.5; c.Scroll.FineGrained = true; });
        _out.Clear();
        p.OnWheel(false, -120, T0, KeyModifiers.None, true, _out);
        Assert.Equal(-60, Assert.Single(_out).X);
    }

    [Fact]
    public void Wheel_Unchanged_PassesThrough()
    {
        var (p, _) = Create(c => c.Scroll.ReverseHorizontal = true);
        _out.Clear();
        Assert.Equal(HookDecision.Pass, p.OnWheel(false, 120, T0, KeyModifiers.None, true, _out));
        Assert.Equal(HookDecision.Block, p.OnWheel(true, 120, T0, KeyModifiers.None, true, _out));
        Assert.Equal((OutputKind.HWheel, -120), (_out[0].Kind, _out[0].X));
    }

    [Fact]
    public void Wheel_HorizontalModifier_ConvertsToHorizontal()
    {
        var (p, _) = Create(c => c.Scroll.HorizontalModifier = HorizontalScrollModifier.Alt);
        _out.Clear();
        Assert.Equal(HookDecision.Pass, p.OnWheel(false, -120, T0, KeyModifiers.None, true, _out));
        Assert.Equal(HookDecision.Block, p.OnWheel(false, -120, T0, KeyModifiers.Alt, true, _out));
        var e = Assert.Single(_out);
        Assert.Equal(OutputKind.HWheel, e.Kind);
        Assert.Equal(120, e.X); // wheel down → scroll right
    }

    [Fact]
    public void Wheel_Acceleration_GrowsWithFastNotches()
    {
        var (p, _) = Create(c => { c.Scroll.AccelerationEnabled = true; c.Scroll.AccelerationStrength = 100; c.Scroll.AccelerationMax = 3; c.Scroll.FineGrained = true; });
        var deltas = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            _out.Clear();
            p.OnWheel(false, 120, T0 + (i * 20 * Ms), KeyModifiers.None, true, _out);
            deltas.Add(_out.Count == 0 ? 120 : _out[0].X);
        }
        Assert.Equal(120, deltas[0]);
        Assert.True(deltas[5] > deltas[1]);
        Assert.Equal(360, deltas[^1]); // capped at 3×
    }

    [Fact]
    public void Wheel_Remap_TriggersOncePerNotch()
    {
        var (p, _) = Create(c => c.Buttons.SetMapping(InputSource.WheelDown, ActionType.Keyboard, new KeyChord(0x20)));
        _out.Clear();
        Assert.Equal(HookDecision.Block, p.OnWheel(false, -60, T0, KeyModifiers.None, true, _out));
        Assert.Empty(_out); // half a notch from a high-resolution wheel
        p.OnWheel(false, -60, T0 + 5 * Ms, KeyModifiers.None, true, _out);
        Assert.Equal(2, _out.Count);
        Assert.Equal(OutputKind.KeyDown, _out[0].Kind);
        Assert.Equal(OutputKind.KeyUp, _out[1].Kind);
        Assert.Equal(InputProcessor.TapHoldUs, _out[1].DueUs - _out[0].DueUs);
    }

    [Fact]
    public void Move_ScalesWithSubPixelCarry()
    {
        var (p, _) = Create(c =>
        {
            c.Sensitivity.DpiStagesEnabled = true;
            c.Sensitivity.NativeDpi = 800;
            c.Sensitivity.Stages = new List<DpiStage> { new(400), new(800) };
            c.Sensitivity.ActiveStage = 0; // 0.5×
        });
        Assert.True(p.WantsMovement);
        int total = 0;
        for (int i = 0; i < 10; i++)
        {
            _out.Clear();
            Assert.Equal(HookDecision.Block, p.OnMove(1, 0, T0 + i, _out));
            total += _out.Sum(e => e.X);
        }
        Assert.Equal(5, total); // 10 counts × 0.5 — no movement lost to rounding
    }

    [Fact]
    public void Move_IndependentXY()
    {
        var (p, _) = Create(c => { c.Sensitivity.LinkXY = false; c.Sensitivity.XSensitivity = 2; c.Sensitivity.YSensitivity = 0.5; });
        _out.Clear();
        p.OnMove(10, 10, T0, _out);
        var e = Assert.Single(_out);
        Assert.Equal((20, 5), (e.X, e.Y));
    }

    [Fact]
    public void Move_UnitySensitivity_IsNotIntercepted()
    {
        var (p, _) = Create(c => { c.Sensitivity.DpiStagesEnabled = true; c.Sensitivity.ActiveStage = 1; });
        Assert.False(p.WantsMovement);
        Assert.Equal(HookDecision.Pass, p.OnMove(3, 3, T0, _out));
    }

    [Fact]
    public void Clutch_UsesClutchDpiWhileHeld()
    {
        var (p, _) = Create(c =>
        {
            c.Sensitivity.NativeDpi = 800;
            c.Sensitivity.ClutchDpi = 400;
            c.Buttons.SetMapping(InputSource.XButton2, ActionType.SensitivityClutch);
        });
        Assert.True(p.WantsMovement);
        _out.Clear();
        Assert.Equal(HookDecision.Pass, p.OnMove(10, 0, T0, _out));
        Button(p, MouseButton.XButton2, true, T0);
        Assert.Equal(ProcessorCommand.ClutchOn, Assert.Single(_out).Command);
        _out.Clear();
        p.OnMove(10, 0, T0 + 1, _out);
        Assert.Equal(5, Assert.Single(_out).X);
        Button(p, MouseButton.XButton2, false, T0 + 2);
        Assert.Equal(ProcessorCommand.ClutchOff, Assert.Single(_out).Command);
    }

    [Fact]
    public void VirtualButton_HoldsMouseButtonWhileKeyHeld()
    {
        var (p, _) = Create(c => c.Buttons.VirtualButtons.Add(new VirtualButton
        {
            Trigger = new KeyChord(KeyNames.VkF13),
            Action = ActionType.BackButton,
        }));
        Assert.True(p.Settings.NeedsKeyboardHook);
        _out.Clear();
        Assert.Equal(HookDecision.Pass, p.OnKey(0x41, true, T0, KeyModifiers.None, _out));
        Assert.Equal(HookDecision.Block, p.OnKey(KeyNames.VkF13, true, T0, KeyModifiers.None, _out));
        Assert.Equal((OutputKind.ButtonDown, MouseButton.XButton1), (_out[0].Kind, _out[0].Button));
        _out.Clear();
        Assert.Equal(HookDecision.Block, p.OnKey(KeyNames.VkF13, true, T0 + 30 * Ms, KeyModifiers.None, _out)); // auto-repeat
        Assert.Empty(_out);
        Assert.Equal(HookDecision.Block, p.OnKey(KeyNames.VkF13, false, T0 + 60 * Ms, KeyModifiers.None, _out));
        Assert.Equal((OutputKind.ButtonUp, MouseButton.XButton1), (_out[0].Kind, _out[0].Button));
    }

    [Fact]
    public void VirtualButton_RequiresExactModifiers_AndMasksAlt()
    {
        var (p, _) = Create(c => c.Buttons.VirtualButtons.Add(new VirtualButton
        {
            Trigger = new KeyChord(0x51, KeyModifiers.Alt), // Alt+Q
            Action = ActionType.ForwardButton,
        }));
        _out.Clear();
        Assert.Equal(HookDecision.Pass, p.OnKey(0x51, true, T0, KeyModifiers.None, _out));
        Assert.Equal(HookDecision.Pass, p.OnKey(0x51, false, T0, KeyModifiers.None, _out));
        Assert.Equal(HookDecision.Block, p.OnKey(0x51, true, T0, KeyModifiers.Alt, _out));
        Assert.Contains(_out, e => e.Command == ProcessorCommand.MaskModifierKey);
    }
}
