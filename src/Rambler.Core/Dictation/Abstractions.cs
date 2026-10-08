namespace Rambler.Core.Dictation;

public enum TargetKind
{
    /// <summary>The focused control is known to accept text.</summary>
    Editable,
    /// <summary>The focus is somewhere that can't take text (desktop, file list, button…).</summary>
    NotEditable,
    /// <summary>Couldn't tell (no accessibility info). Treated as able to receive text.</summary>
    Unknown,
}

/// <summary>
/// Where the text should go, captured when dictation starts. A window handle alone doesn't identify a
/// text field (browsers and Electron apps use one window for every field), so the focused control's
/// handle and its UI Automation identity are kept as well, when available.
/// </summary>
public sealed record InsertionTarget(nint WindowHandle, int ProcessId, string Description)
{
    public static InsertionTarget None { get; } = new(0, 0, "no window") { Kind = TargetKind.NotEditable, AppName = "No editable target" };

    /// <summary>Short user-facing app name, e.g. "Discord" or "Notepad".</summary>
    public string AppName { get; init; } = string.Empty;
    public TargetKind Kind { get; init; } = TargetKind.Unknown;
    /// <summary>Win32 handle of the focused control (may equal the window for browser-like apps).</summary>
    public nint FocusHandle { get; init; }
    /// <summary>UI Automation runtime id of the focused element, when available.</summary>
    public string? ElementKey { get; init; }

    public bool IsNone => WindowHandle == 0;
    public bool CanReceiveText => !IsNone && Kind != TargetKind.NotEditable;
    public string DisplayName => CanReceiveText ? (AppName.Length > 0 ? AppName : Description) : "No editable target";
}

public enum InsertionOutcome
{
    Inserted,
    /// <summary>The target isn't focused (or keys are held). Nothing — or only <c>CharsInserted</c> — was typed.</summary>
    TargetNotReady,
    /// <summary>Not typed into the target, but placed on the clipboard for manual pasting.</summary>
    CopiedToClipboard,
    Failed,
}

public sealed record InsertionResult(InsertionOutcome Outcome, string? Message = null, int CharsInserted = 0)
{
    public static InsertionResult Success { get; } = new(InsertionOutcome.Inserted);
}

/// <param name="AllowRefocus">May bring the target window back to the foreground once (explicit user action).</param>
/// <param name="CopyOnFailure">Put the text on the clipboard if it can't be inserted.</param>
public sealed record InsertOptions(bool AllowRefocus, bool CopyOnFailure)
{
    /// <summary>The user asked for this insertion (Stop from the popup, Insert button).</summary>
    public static InsertOptions Interactive { get; } = new(AllowRefocus: true, CopyOnFailure: true);

    /// <summary>Background progressive insertion: never steal focus, never touch the clipboard on failure.</summary>
    public static InsertOptions Progressive { get; } = new(AllowRefocus: false, CopyOnFailure: false);
}

/// <summary>Delivers final text to another application. Implementations must never type into an unverified target.</summary>
public interface ITextInserter
{
    /// <summary>Captures the control that should receive text (ignoring Rambler's own windows).</summary>
    InsertionTarget CaptureTarget();

    /// <summary>True when the captured window is in the foreground and the same control still has focus.</summary>
    bool IsTargetReady(InsertionTarget target);

    /// <summary>True when one of Rambler's own windows (e.g. the popup) is in the foreground.</summary>
    bool IsOwnWindowActive { get; }

    /// <summary>Raised (on any thread) when the foreground window changes, so waiting insertion can resume promptly.</summary>
    event Action? FocusChanged;

    Task<InsertionResult> InsertAsync(InsertionTarget target, string text, InsertOptions options, CancellationToken ct);

    /// <summary>Copies text for manual pasting. Returns false if the clipboard was unavailable.</summary>
    bool CopyToClipboard(string text);
}
