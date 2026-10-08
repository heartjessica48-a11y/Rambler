namespace Rambler.Core.Dictation;

/// <summary>Where the text should go: captured when dictation starts.</summary>
public sealed record InsertionTarget(nint WindowHandle, int ProcessId, string Description)
{
    public static InsertionTarget None { get; } = new(0, 0, "no window");
    public bool IsNone => WindowHandle == 0;
}

public enum InsertionOutcome
{
    Inserted,
    /// <summary>Not typed into the target, but placed on the clipboard for manual pasting.</summary>
    CopiedToClipboard,
    Failed,
}

public sealed record InsertionResult(InsertionOutcome Outcome, string? Message = null)
{
    public static InsertionResult Success { get; } = new(InsertionOutcome.Inserted);
}

/// <summary>Delivers final text to another application. Implementations must never type into an unverified window.</summary>
public interface ITextInserter
{
    /// <summary>Captures the window that should receive text (ignoring Rambler's own windows).</summary>
    InsertionTarget CaptureTarget();

    Task<InsertionResult> InsertAsync(InsertionTarget target, string text, CancellationToken ct);

    /// <summary>Copies text for manual pasting. Returns false if the clipboard was unavailable.</summary>
    bool CopyToClipboard(string text);
}
