using System.Diagnostics;
using System.Runtime.InteropServices;
using Rambler.Core;
using Rambler.Core.Dictation;
using Rambler.Core.Settings;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

/// <summary>
/// Inserts text into the control captured when dictation started.
/// <list type="bullet">
/// <item>Never types unless that window is in the foreground with the same control focused
/// (<see cref="TargetWindowService.IsReady"/>); focus is re-checked between typing batches.</item>
/// <item>Background (progressive) insertion never steals focus and never touches the clipboard on failure:
/// it reports <see cref="InsertionOutcome.TargetNotReady"/> and the caller keeps the text.</item>
/// <item>Line breaks never use a bare Enter, which sends messages in chat apps, except in classic editors
/// where Enter is a line break. Elsewhere multi-line text is pasted, which inserts line breaks without submitting.</item>
/// </list>
/// Only SendInput (Unicode) and the clipboard are used: no hooks, no injection, no elevation.
/// </summary>
public sealed class TextInsertionService : ITextInserter
{
    private const int CharsPerBatch = 48;

    private enum NewlineKey { Enter, ShiftEnter }

    private readonly Func<AppSettings> _settings;
    private readonly Func<Task> _hideOwnWindows;
    private readonly TargetWindowService _targets;

    /// <param name="hideOwnWindows">Hides Rambler's popup (on the UI thread) after focus is handed back.</param>
    public TextInsertionService(Func<AppSettings> settings, Func<Task> hideOwnWindows, TargetWindowService targets)
    {
        _settings = settings;
        _hideOwnWindows = hideOwnWindows;
        _targets = targets;
    }

    public event Action? FocusChanged
    {
        add => _targets.ForegroundChanged += value;
        remove => _targets.ForegroundChanged -= value;
    }

    public bool IsOwnWindowActive => _targets.IsOwnWindowActive;

    public InsertionTarget CaptureTarget() => _targets.Capture();

    public bool IsTargetReady(InsertionTarget target) => _targets.IsReady(target);

    public async Task<InsertionResult> InsertAsync(InsertionTarget target, string text, InsertOptions options, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return InsertionResult.Success;

        if (target.IsNone || !IsWindow(target.WindowHandle) || WindowPid(target.WindowHandle) != target.ProcessId)
            return Unavailable(text, options, "The window you were dictating into is gone.", final: true);

        if (target.Kind == TargetKind.NotEditable)
            return Unavailable(text, options, $"{target.DisplayName} doesn't have a text field focused.", final: true);

        if (!Environment.IsPrivilegedProcess && IsElevatedOrProtected((uint)target.ProcessId))
            return Unavailable(text, options, $"{target.DisplayName} runs as administrator, so Windows blocks typing into it.", final: true);

        if (!_targets.IsReady(target))
        {
            if (!options.AllowRefocus || !await RefocusAsync(target, ct).ConfigureAwait(false))
                return Unavailable(text, options, $"{target.DisplayName} isn't focused.", final: false);
        }
        else if (options.AllowRefocus)
        {
            await _hideOwnWindows().ConfigureAwait(false);
        }

        if (!await WaitForModifiersReleasedAsync(ct).ConfigureAwait(false))
            return Unavailable(text, options, "Keys were still held down, so nothing was typed.", final: false);

        var settings = _settings();
        if (settings.InsertionMethod == InsertionMethod.Paste)
            return await PasteAsync(target, text, settings.RestoreClipboard, ct).ConfigureAwait(false);
        if (!text.Contains('\n'))
            return await TypeAsync(target, text, NewlineKey.Enter, ct).ConfigureAwait(false);
        if (settings.InsertionMethod == InsertionMethod.Type)
            return await TypeAsync(target, text, NewlineKey.ShiftEnter, ct).ConfigureAwait(false);
        return _targets.IsPlainEditor(target)
            ? await TypeAsync(target, text, NewlineKey.Enter, ct).ConfigureAwait(false)
            : await PasteAsync(target, text, settings.RestoreClipboard, ct).ConfigureAwait(false);
    }

    public bool CopyToClipboard(string text) => ClipboardHelper.SetText(text, excludeFromHistory: false) != 0;

    // ---- Typing ---------------------------------------------------------------------------------

    /// <summary>Types in small batches, re-checking focus before each; reports how much was typed if interrupted.</summary>
    private async Task<InsertionResult> TypeAsync(InsertionTarget target, string text, NewlineKey newline, CancellationToken ct)
    {
        var size = Marshal.SizeOf<INPUT>();
        var typed = 0;
        while (typed < text.Length)
        {
            var count = Math.Min(CharsPerBatch, text.Length - typed);
            if (typed + count < text.Length && char.IsHighSurrogate(text[typed + count - 1])) count++; // keep pairs together

            if (!_targets.IsReady(target, thorough: false))
                return new InsertionResult(InsertionOutcome.TargetNotReady, "Focus moved while typing.", typed);

            var inputs = BuildInputs(text.AsSpan(typed, count), newline);
            if (inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, size) == 0)
            {
                return typed == 0
                    ? new InsertionResult(InsertionOutcome.Failed, "Windows blocked typing into this app.")
                    : new InsertionResult(InsertionOutcome.TargetNotReady, "Typing was interrupted.", typed);
            }
            typed += count;
            if (typed < text.Length) await Task.Delay(5, ct).ConfigureAwait(false);
        }
        return InsertionResult.Success;
    }

    private static INPUT[] BuildInputs(ReadOnlySpan<char> text, NewlineKey newline)
    {
        var list = new List<INPUT>(text.Length * 2 + 4);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                if (newline == NewlineKey.ShiftEnter) list.Add(Key(VK_SHIFT_U, 0, 0));
                list.Add(Key(VK_RETURN, 0, 0));
                list.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP));
                if (newline == NewlineKey.ShiftEnter) list.Add(Key(VK_SHIFT_U, 0, KEYEVENTF_KEYUP));
                continue;
            }
            // Surrogate pairs are sent as two UTF-16 units; Windows recombines them.
            list.Add(Key(0, c, KEYEVENTF_UNICODE));
            list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        return [.. list];
    }

    private const ushort VK_SHIFT_U = 0x10;

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
            return new InsertionResult(InsertionOutcome.TargetNotReady, "The clipboard is in use by another app.");

        if (!_targets.IsReady(target, thorough: false))
        {
            if (canRestore) ClipboardHelper.Restore(snapshot!);
            return new InsertionResult(InsertionOutcome.TargetNotReady, "Focus moved before pasting.");
        }

        INPUT[] chord =
        [
            Key(VK_CONTROL, 0, 0), Key(VK_V, 0, 0),
            Key(VK_V, 0, KEYEVENTF_KEYUP), Key(VK_CONTROL, 0, KEYEVENTF_KEYUP),
        ];
        if (SendInput((uint)chord.Length, chord, Marshal.SizeOf<INPUT>()) == 0)
        {
            if (canRestore) ClipboardHelper.Restore(snapshot!);
            return new InsertionResult(InsertionOutcome.Failed, "Windows blocked pasting into this app.");
        }

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

    /// <summary>
    /// One attempt to hand focus back (explicit user action only). Foreground is set while Rambler's popup
    /// still owns it (Windows only lets the foreground app do that), and the popup is hidden afterwards.
    /// </summary>
    private async Task<bool> RefocusAsync(InsertionTarget target, CancellationToken ct)
    {
        if (IsIconic(target.WindowHandle)) ShowWindow(target.WindowHandle, SW_RESTORE);
        SetForegroundWindow(target.WindowHandle);
        await _hideOwnWindows().ConfigureAwait(false);
        for (var i = 0; i < 20; i++)
        {
            if (_targets.IsReady(target)) return true;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return _targets.IsReady(target);
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

    /// <summary>
    /// The text can't go in now. Interactive inserts fall back to the clipboard; background inserts report it
    /// (TargetNotReady: try again later; Failed: the target can never take it).
    /// </summary>
    private InsertionResult Unavailable(string text, InsertOptions options, string reason, bool final)
    {
        if (options.CopyOnFailure)
        {
            AppLog.Warn("Insertion fallback: " + reason);
            return CopyToClipboard(text)
                ? new InsertionResult(InsertionOutcome.CopiedToClipboard, reason + " Text copied: press Ctrl+V to paste.")
                : new InsertionResult(InsertionOutcome.Failed, reason);
        }
        return new InsertionResult(final ? InsertionOutcome.Failed : InsertionOutcome.TargetNotReady, reason);
    }

    private static int WindowPid(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }
}
