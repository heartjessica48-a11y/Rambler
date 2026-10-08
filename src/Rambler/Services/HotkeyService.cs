using System.Runtime.InteropServices;
using System.Windows.Interop;
using Rambler.Core.Settings;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

public enum HotkeyAction { ToggleDefault = 1, ToggleSmartBypass = 2 }

public sealed record HotkeyRegistration(HotkeyAction Action, string Gesture, bool Success, string? Error)
{
    /// <summary>No shortcut configured (allowed: dictation can be started from the popup).</summary>
    public bool IsDisabled => Success && Gesture.Length == 0;
}

/// <summary>
/// System-wide hotkeys via Win32 RegisterHotKey on a hidden message-only window.
/// The OS delivers WM_HOTKEY; nothing is polled and no keyboard hook is installed.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<HotkeyAction, HotkeyGesture> _registered = [];
    private bool _suspended;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("Rambler.Hotkeys")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE: message-only window
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyAction>? Pressed;

    public nint Handle => _source.Handle;

    /// <summary>
    /// Registers both hotkeys, reporting conflicts with Windows or other applications.
    /// An empty shortcut is disabled and simply not registered.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Apply(string toggle, string bypass)
    {
        UnregisterAll();
        var results = new List<HotkeyRegistration>(2);
        HotkeyGesture.TryParse(toggle, out var toggleGesture);
        HotkeyGesture.TryParse(bypass, out var bypassGesture);

        results.Add(Register(HotkeyAction.ToggleDefault, toggle, toggleGesture));
        if (bypassGesture.IsValid && bypassGesture == toggleGesture)
            results.Add(new HotkeyRegistration(HotkeyAction.ToggleSmartBypass, bypass, false, "Same shortcut as the main hotkey."));
        else
            results.Add(Register(HotkeyAction.ToggleSmartBypass, bypass, bypassGesture));
        return results;
    }

    /// <summary>Temporarily releases hotkeys (e.g. while the user records a new shortcut).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (var action in _registered.Keys) UnregisterHotKey(Handle, (int)action);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var (action, g) in _registered)
            RegisterHotKey(Handle, (int)action, (uint)g.Modifiers | MOD_NOREPEAT, (uint)g.VirtualKey);
    }

    private HotkeyRegistration Register(HotkeyAction action, string text, HotkeyGesture gesture)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new HotkeyRegistration(action, string.Empty, true, null);
        if (!gesture.IsValid)
            return new HotkeyRegistration(action, text, false, "Not a valid shortcut.");

        if (RegisterHotKey(Handle, (int)action, (uint)gesture.Modifiers | MOD_NOREPEAT, (uint)gesture.VirtualKey))
        {
            _registered[action] = gesture;
            return new HotkeyRegistration(action, gesture.ToString(), true, null);
        }

        var error = Marshal.GetLastWin32Error();
        var message = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"{gesture} is already used by Windows or another app. Record a different shortcut, or clear it."
            : $"Couldn't register {gesture} (error {error}).";
        return new HotkeyRegistration(action, gesture.ToString(), false, message);
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered.Keys) UnregisterHotKey(Handle, (int)action);
        _registered.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && !_suspended)
        {
            var id = (int)wParam;
            if (Enum.IsDefined(typeof(HotkeyAction), id))
            {
                handled = true;
                Pressed?.Invoke((HotkeyAction)id);
            }
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
