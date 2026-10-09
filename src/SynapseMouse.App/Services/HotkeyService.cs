using System.Runtime.InteropServices;
using SynapseMouse.App.Infrastructure;
using SynapseMouse.Core.Models;
using static SynapseMouse.App.Native.NativeMethods;

namespace SynapseMouse.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey (config switching, DPI stages, Master Enable, the Single → Double
/// click toggle). Hotkeys only switch settings; they never generate game input.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    public const string EmergencyId = "emergency";
    /// <summary>Hotkey id of the Single → Double click on/off toggle.</summary>
    public const string DoubleClickToggleId = "double-click-toggle";

    private readonly MessageWindow _window;
    private readonly Dictionary<int, string> _registered = new();
    private int _nextId = 1;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    public event Action<string>? HotkeyPressed;

    /// <summary>Hotkey ids that could not be registered (usually taken by another app).</summary>
    public IReadOnlySet<string> Failed { get; private set; } = new HashSet<string>();

    /// <summary>The fixed emergency shortcut: Ctrl + Alt + Shift + F12.</summary>
    public static KeyChord EmergencyChord => SynapseMouse.Core.Input.InputSafety.EmergencyChord;

    /// <summary>Replaces all registrations.</summary>
    public void SetHotkeys(IEnumerable<(string Id, KeyChord? Chord)> hotkeys)
    {
        UnregisterAll();
        var failed = new HashSet<string>();
        var used = new HashSet<(int, KeyModifiers)>();
        foreach (var (id, chord) in hotkeys)
        {
            if (chord is null || !chord.IsValidTrigger)
            {
                continue;
            }
            if (!used.Add((chord.Key, chord.Modifiers)))
            {
                failed.Add(id); // Same combination already used by another Synapse hotkey.
                continue;
            }

            int nativeId = _nextId++;
            if (_nextId > 0xBFFF)
            {
                _nextId = 1;
            }
            if (RegisterHotKey(_window.Handle, nativeId, ToNativeModifiers(chord.Modifiers) | MOD_NOREPEAT, (uint)chord.Key))
            {
                _registered[nativeId] = id;
            }
            else
            {
                failed.Add(id);
                Log.Warn($"Hotkey {chord} ({id}) could not be registered (error {Marshal.GetLastWin32Error()}).");
            }
        }
        Failed = failed;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.MessageReceived -= OnMessage;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _registered.Clear();
    }

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out string? id))
        {
            handled = true;
            HotkeyPressed?.Invoke(id);
        }
    }

    private static uint ToNativeModifiers(KeyModifiers modifiers)
    {
        uint m = 0;
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            m |= MOD_ALT;
        }
        if (modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            m |= MOD_CONTROL;
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            m |= MOD_SHIFT;
        }
        if (modifiers.HasFlag(KeyModifiers.Win))
        {
            m |= MOD_WIN;
        }
        return m;
    }
}
