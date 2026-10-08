using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rambler.Core;
using Rambler.Core.Dictation;
using Rambler.Core.Settings;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

/// <summary>
/// Inserts text into the window that was focused when dictation started.
/// Safety rules: never type unless that exact window is verified to be in the foreground again;
/// otherwise put the text on the clipboard and tell the user. Uses only SendInput (Unicode) and the
/// clipboard; no hooks, no injection, no elevation.
/// </summary>
public sealed class TextInsertionService : ITextInserter, IDisposable
{
    private const int TypeMaxLength = 300;
    private const int InputsPerBatch = 100;

    private readonly Func<AppSettings> _settings;
    private readonly Func<Task> _hideOwnWindows;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly WinEventProc _foregroundProc;
    private readonly nint _hook;
    private nint _lastExternalWindow;

    /// <param name="hideOwnWindows">Hides Rambler's popup (on the UI thread) before focus is handed back.</param>
    public TextInsertionService(Func<AppSettings> settings, Func<Task> hideOwnWindows)
    {
        _settings = settings;
        _hideOwnWindows = hideOwnWindows;
        _foregroundProc = OnForegroundChanged; // keep the delegate alive
        _lastExternalWindow = GetForegroundWindow();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public InsertionTarget CaptureTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || IsOwnWindow(hwnd)) hwnd = _lastExternalWindow;
        if (hwnd == 0 || !IsWindow(hwnd)) return InsertionTarget.None;

        hwnd = GetAncestor(hwnd, GA_ROOT) is var root && root != 0 ? root : hwnd;
        GetWindowThreadProcessId(hwnd, out var pid);
        return new InsertionTarget(hwnd, (int)pid, Describe(hwnd, pid));
    }

    public async Task<InsertionResult> InsertAsync(InsertionTarget target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return InsertionResult.Success;

        if (target.IsNone || !IsWindow(target.WindowHandle) || WindowPid(target.WindowHandle) != target.ProcessId)
            return CopyInstead(text, "The window you were dictating into is gone.");

        if (!Environment.IsPrivilegedProcess && IsElevatedOrProtected((uint)target.ProcessId))
            return CopyInstead(text, $"{target.Description} runs as administrator, so Windows blocks typing into it.");

        await _hideOwnWindows().ConfigureAwait(false);

        if (!await WaitForModifiersReleasedAsync(ct).ConfigureAwait(false))
            return CopyInstead(text, "Keys were still held down, so nothing was typed.");

        if (!await FocusAsync(target.WindowHandle, ct).ConfigureAwait(false))
            return CopyInstead(text, $"Couldn't switch back to {target.Description}.");

        var settings = _settings();
        var paste = settings.InsertionMethod switch
        {
            InsertionMethod.Paste => true,
            InsertionMethod.Type => false,
            _ => text.Contains('\n') || text.Length > TypeMaxLength,
        };

        return paste
            ? await PasteAsync(target, text, settings.RestoreClipboard, ct).ConfigureAwait(false)
            : await TypeAsync(target, text, ct).ConfigureAwait(false);
    }

    public bool CopyToClipboard(string text) => ClipboardHelper.SetText(text, excludeFromHistory: false) != 0;

    // ---- Typing ---------------------------------------------------------------------------------

    private async Task<InsertionResult> TypeAsync(InsertionTarget target, string text, CancellationToken ct)
    {
        var inputs = BuildUnicodeInputs(text);
        var size = Marshal.SizeOf<INPUT>();
        for (var offset = 0; offset < inputs.Length; offset += InputsPerBatch)
        {
            if (!IsForeground(target.WindowHandle))
                return CopyInstead(text, "Focus changed while typing. The full text was copied instead.");

            var batch = inputs.AsSpan(offset, Math.Min(InputsPerBatch, inputs.Length - offset)).ToArray();
            var sent = SendInput((uint)batch.Length, batch, size);
            if (sent == 0)
                return CopyInstead(text, "Windows blocked typing into this app.");
            if (offset + InputsPerBatch < inputs.Length) await Task.Delay(5, ct).ConfigureAwait(false);
        }
        return InsertionResult.Success;
    }

    internal static INPUT[] BuildUnicodeInputs(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                list.Add(Key(VK_RETURN, 0, 0));
                list.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP));
                continue;
            }
            // Surrogate pairs are sent as two UTF-16 units; Windows recombines them.
            list.Add(Key(0, c, KEYEVENTF_UNICODE));
            list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        return [.. list];
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    // ---- Pasting --------------------------------------------------------------------------------

    private async Task<InsertionResult> PasteAsync(InsertionTarget target, string text, bool restore, CancellationToken ct)
    {
        var snapshot = restore ? ClipboardHelper.TakeSnapshot() : null;
        var canRestore = snapshot is { Restorable: true };

        var sequence = ClipboardHelper.SetText(text, excludeFromHistory: canRestore);
        if (sequence == 0)
            return new InsertionResult(InsertionOutcome.Failed, "The clipboard is in use by another app.");

        if (!IsForeground(target.WindowHandle))
            return new InsertionResult(InsertionOutcome.CopiedToClipboard, "Focus changed, so nothing was pasted. The text is on the clipboard: press Ctrl+V.");

        INPUT[] chord =
        [
            Key(VK_CONTROL, 0, 0), Key(VK_V, 0, 0),
            Key(VK_V, 0, KEYEVENTF_KEYUP), Key(VK_CONTROL, 0, KEYEVENTF_KEYUP),
        ];
        if (SendInput((uint)chord.Length, chord, Marshal.SizeOf<INPUT>()) == 0)
            return new InsertionResult(InsertionOutcome.CopiedToClipboard, "Windows blocked pasting. The text is on the clipboard: press Ctrl+V.");

        if (!canRestore)
        {
            return snapshot is null
                ? InsertionResult.Success
                : new InsertionResult(InsertionOutcome.Inserted, "Your previous clipboard item couldn't be restored.");
        }

        // Give the target app time to read the clipboard, then restore unless someone else changed it.
        await Task.Delay(800, CancellationToken.None).ConfigureAwait(false);
        if (GetClipboardSequenceNumber() == sequence) ClipboardHelper.Restore(snapshot!);
        return InsertionResult.Success;
    }

    // ---- Focus & safety -----------------------------------------------------------------------

    private static async Task<bool> FocusAsync(nint hwnd, CancellationToken ct)
    {
        if (IsForeground(hwnd)) return true;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        for (var i = 0; i < 20; i++)
        {
            if (IsForeground(hwnd)) return true;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return IsForeground(hwnd);
    }

    private static bool IsForeground(nint hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        return fg == hwnd || GetAncestor(fg, GA_ROOT) == hwnd;
    }

    /// <summary>Waits briefly for the user to release the hotkey's modifiers so they don't combine with typed keys.</summary>
    private static async Task<bool> WaitForModifiersReleasedAsync(CancellationToken ct)
    {
        int[] keys = [VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            if (!keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return true;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsElevatedOrProtected(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return true;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return true; // access denied: elevated
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private InsertionResult CopyInstead(string text, string reason)
    {
        AppLog.Warn("Insertion fallback: " + reason);
        return CopyToClipboard(text)
            ? new InsertionResult(InsertionOutcome.CopiedToClipboard, reason + " Text copied: press Ctrl+V to paste.")
            : new InsertionResult(InsertionOutcome.Failed, reason);
    }

    private bool IsOwnWindow(nint hwnd) => WindowPid(hwnd) == _ownProcessId;

    private static int WindowPid(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    private void OnForegroundChanged(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != 0 && !IsOwnWindow(hwnd)) _lastExternalWindow = hwnd;
    }

    private static string Describe(nint hwnd, uint pid)
    {
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        var exe = ProcessName(pid);
        var t = title.ToString().Trim();
        if (t.Length > 40) t = t[..40] + "…";
        return exe is null ? (t.Length > 0 ? t : "the target window") : t.Length > 0 ? $"{exe} ({t})" : exe;
    }

    private static string? ProcessName(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? Path.GetFileNameWithoutExtension(sb.ToString()) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
    }
}
