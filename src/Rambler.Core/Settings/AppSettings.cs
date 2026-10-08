using Rambler.Core.Dictation;

namespace Rambler.Core.Settings;

public enum ThemePreference { System, Light, Dark }

public enum InsertionMethod
{
    /// <summary>Type text; for line breaks, use Enter only in plain editors and paste elsewhere (never submits).</summary>
    Auto,
    Type,
    Paste,
}

/// <summary>
/// Persisted user settings. Deliberately contains NO secrets: the API key lives in
/// Windows Credential Manager (see <see cref="ICredentialStore"/>).
/// </summary>
public sealed class AppSettings
{
    public const string DefaultLiveModel = "gemini-3.5-transcribe-live";
    public const string DefaultRecordedModel = "gemini-3.5-transcribe";
    public const string DefaultCleanupModel = "gemini-3.6-flash";
    public const string DefaultThinkingLevel = "MINIMAL";
    public const int DefaultTechnicalRefinement = 35;

    /// <summary>2: shortcuts are off by default (the old Ctrl+Win+Space defaults clash with Windows).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 2;

    // General
    public bool StartWithWindows { get; set; }
    public DictationMode DefaultMode { get; set; } = DictationMode.PromptCleanup;
    /// <summary>Main shortcut; empty (the default) disables it. The user records their own.</summary>
    public string HotkeyToggle { get; set; } = string.Empty;
    /// <summary>Smart Only bypass shortcut; empty (the default) disables it.</summary>
    public string HotkeySmartBypass { get; set; } = string.Empty;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool PlaySounds { get; set; }
    public InsertionMethod InsertionMethod { get; set; } = InsertionMethod.Auto;
    /// <summary>
    /// False (default): progressive insertion, text appears as you speak. True: buffer the whole dictation
    /// and insert it at the end (best whole-session coherence).
    /// </summary>
    public bool InsertWhenFinished { get; set; }
    public bool RestoreClipboard { get; set; } = true;

    // Audio
    /// <summary>WASAPI endpoint id; null means the Windows default communications microphone.</summary>
    public string? MicrophoneDeviceId { get; set; }
    public bool AutoStopOnSilence { get; set; }
    public int SilenceTimeoutSeconds { get; set; } = 10;
    public int MaxRecordingMinutes { get; set; } = 30;

    // Gemini
    public string LiveModel { get; set; } = DefaultLiveModel;
    public string RecordedModel { get; set; } = DefaultRecordedModel;
    public bool UseRecordedFallback { get; set; } = true;
    /// <summary>BCP-47 code (e.g. "en-US"); empty means automatic language detection.</summary>
    public string LanguageCode { get; set; } = string.Empty;
    public List<string> CustomVocabulary { get; set; } = [];

    // Cleanup
    public string CleanupModel { get; set; } = DefaultCleanupModel;
    /// <summary>"MINIMAL", "LOW", "MEDIUM", "HIGH" or empty for the model default.</summary>
    public string CleanupThinkingLevel { get; set; } = DefaultThinkingLevel;
    /// <summary>Null means "use the built-in default prompt".</summary>
    public string? CleanupPrompt { get; set; }
    public int TechnicalRefinement { get; set; } = DefaultTechnicalRefinement;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.CustomVocabulary = [.. CustomVocabulary];
        return copy;
    }

    /// <summary>Clamps out-of-range values from hand-edited or older files.</summary>
    public void Normalize()
    {
        TechnicalRefinement = Math.Clamp(TechnicalRefinement, 0, 100);
        SilenceTimeoutSeconds = Math.Clamp(SilenceTimeoutSeconds, 2, 120);
        MaxRecordingMinutes = Math.Clamp(MaxRecordingMinutes, 1, 120);
        if (string.IsNullOrWhiteSpace(LiveModel)) LiveModel = DefaultLiveModel;
        if (string.IsNullOrWhiteSpace(RecordedModel)) RecordedModel = DefaultRecordedModel;
        if (string.IsNullOrWhiteSpace(CleanupModel)) CleanupModel = DefaultCleanupModel;
        // Empty shortcut = disabled (the user can always dictate from the popup button).
        HotkeyToggle = HotkeyToggle?.Trim() ?? string.Empty;
        HotkeySmartBypass = HotkeySmartBypass?.Trim() ?? string.Empty;
        LanguageCode = LanguageCode?.Trim() ?? string.Empty;
        CleanupThinkingLevel = (CleanupThinkingLevel ?? string.Empty).Trim().ToUpperInvariant();
        CustomVocabulary = (CustomVocabulary ?? [])
            .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (CleanupPrompt is not null && string.IsNullOrWhiteSpace(CleanupPrompt)) CleanupPrompt = null;
    }
}
