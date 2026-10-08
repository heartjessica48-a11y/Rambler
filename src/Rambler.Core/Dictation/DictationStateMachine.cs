namespace Rambler.Core.Dictation;

public enum DictationMode
{
    /// <summary>Microphone → SMART transcription → Gemini Flash cleanup → insertion.</summary>
    PromptCleanup,

    /// <summary>Microphone → SMART transcription → insertion (no second model).</summary>
    SmartOnly,
}

public enum DictationTrigger
{
    /// <summary>Main hotkey or the popup button: uses the selected mode.</summary>
    Default,

    /// <summary>Bypass hotkey: Smart Only for this dictation without changing the selected mode.</summary>
    SmartBypass,
}

public enum DictationState
{
    Idle,
    Listening,
    Finalizing,
    Cleaning,
    Inserting,
    Error,
}

/// <summary>
/// Idle → Listening → Finalizing → Cleaning (optional) → Inserting → Idle, plus Error and cancellation.
/// Pure and synchronous so the allowed transitions are easy to test.
/// </summary>
public sealed class DictationStateMachine
{
    private static readonly Dictionary<DictationState, DictationState[]> s_allowed = new()
    {
        [DictationState.Idle] = [DictationState.Listening, DictationState.Error],
        [DictationState.Listening] = [DictationState.Finalizing, DictationState.Idle, DictationState.Error],
        [DictationState.Finalizing] = [DictationState.Cleaning, DictationState.Inserting, DictationState.Idle, DictationState.Error],
        [DictationState.Cleaning] = [DictationState.Inserting, DictationState.Idle, DictationState.Error],
        [DictationState.Inserting] = [DictationState.Idle, DictationState.Error],
        [DictationState.Error] = [DictationState.Idle, DictationState.Listening, DictationState.Inserting, DictationState.Error],
    };

    public DictationState State { get; private set; } = DictationState.Idle;

    public bool IsBusy => State is DictationState.Finalizing or DictationState.Cleaning or DictationState.Inserting;

    public bool CanStart => State is DictationState.Idle or DictationState.Error;

    public static DictationMode ResolveMode(DictationTrigger trigger, DictationMode selected) =>
        trigger == DictationTrigger.SmartBypass ? DictationMode.SmartOnly : selected;

    public bool CanTransition(DictationState to) => to == State || s_allowed[State].Contains(to);

    public void TransitionTo(DictationState to)
    {
        if (!CanTransition(to))
            throw new InvalidOperationException($"Invalid dictation transition {State} → {to}.");
        State = to;
    }
}
