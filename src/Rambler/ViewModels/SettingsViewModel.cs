using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Rambler.Core;
using Rambler.Core.Audio;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Services;

namespace Rambler.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly WindowsCredentialStore _credentials;
    private readonly GeminiConnectionTester _tester;
    private readonly Func<string, string, IReadOnlyList<HotkeyRegistration>> _applyHotkeys;
    private readonly Action<ThemePreference> _applyTheme;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _s;

    private string _hotkeyToggle;
    private string _hotkeyBypass;
    private string? _toggleStatus;
    private bool _toggleStatusIsError;
    private string? _bypassStatus;
    private bool _bypassStatusIsError;
    private double _micLevel;
    private string? _micTestStatus;
    private bool _micTesting;
    private string? _apiKeyStatus;
    private string _pendingApiKey = string.Empty;
    private string? _connectionStatus;
    private bool _testingConnection;
    private string _vocabularyText;
    private string _cleanupPrompt;
    private string? _saveStatus;

    public SettingsViewModel(SettingsService settingsService, WindowsCredentialStore credentials, GeminiConnectionTester tester,
        Func<string, string, IReadOnlyList<HotkeyRegistration>> applyHotkeys, Action<ThemePreference> applyTheme,
        IReadOnlyList<HotkeyRegistration> currentHotkeys, string? lastError, Dispatcher dispatcher)
    {
        _settingsService = settingsService;
        _credentials = credentials;
        _tester = tester;
        _applyHotkeys = applyHotkeys;
        _applyTheme = applyTheme;
        _dispatcher = dispatcher;
        _s = settingsService.Current;
        _s.StartWithWindows = StartupRegistration.IsEnabled();

        _hotkeyToggle = _s.HotkeyToggle;
        _hotkeyBypass = _s.HotkeySmartBypass;
        ShowHotkeyResults(currentHotkeys);
        _vocabularyText = string.Join(", ", _s.CustomVocabulary);
        _cleanupPrompt = _s.CleanupPrompt ?? DefaultPrompts.CleanupSystemPrompt;
        LastError = lastError;

        Devices = [new Choice<string?>(null, "System default microphone")];
        foreach (var d in WasapiAudioSource.GetDevices()) Devices.Add(new Choice<string?>(d.Id, d.Name));
        if (_s.MicrophoneDeviceId is not null && Devices.All(d => d.Value != _s.MicrophoneDeviceId))
            Devices.Add(new Choice<string?>(_s.MicrophoneDeviceId, "Unavailable microphone (default will be used)"));
        SelectedDevice = Devices.First(d => d.Value == _s.MicrophoneDeviceId);

        RefreshApiKeyStatus();

        TestMicrophoneCommand = new AsyncCommand(TestMicrophoneAsync, () => !_micTesting);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => !_testingConnection);
        RemoveApiKeyCommand = new RelayCommand(RemoveApiKey);
        RestoreDefaultPromptCommand = new RelayCommand(() => CleanupPrompt = DefaultPrompts.CleanupSystemPrompt);
    }

    public event Action? CloseRequested;

    public ICommand TestMicrophoneCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand RemoveApiKeyCommand { get; }
    public ICommand RestoreDefaultPromptCommand { get; }

    // ---- General ----
    public bool StartWithWindows { get => _s.StartWithWindows; set { _s.StartWithWindows = value; OnPropertyChanged(); } }

    public IReadOnlyList<Choice<DictationMode>> Modes { get; } =
        [new(DictationMode.PromptCleanup, "Prompt Cleanup"), new(DictationMode.SmartOnly, "Smart Only")];
    public Choice<DictationMode> SelectedMode
    {
        get => Modes.First(m => m.Value == _s.DefaultMode);
        set { _s.DefaultMode = value.Value; OnPropertyChanged(); }
    }

    public IReadOnlyList<Choice<ThemePreference>> Themes { get; } =
        [new(ThemePreference.System, "Use Windows setting"), new(ThemePreference.Light, "Light"), new(ThemePreference.Dark, "Dark")];
    public Choice<ThemePreference> SelectedTheme
    {
        get => Themes.First(t => t.Value == _s.Theme);
        set { _s.Theme = value.Value; OnPropertyChanged(); }
    }

    public IReadOnlyList<Choice<InsertionMethod>> InsertionMethods { get; } =
    [
        new(InsertionMethod.Auto, "Automatic (type text; paste line breaks safely)"),
        new(InsertionMethod.Type, "Always type (line breaks as Shift+Enter)"),
        new(InsertionMethod.Paste, "Always paste via clipboard"),
    ];
    public Choice<InsertionMethod> SelectedInsertionMethod
    {
        get => InsertionMethods.First(m => m.Value == _s.InsertionMethod);
        set { _s.InsertionMethod = value.Value; OnPropertyChanged(); }
    }

    public bool RestoreClipboard { get => _s.RestoreClipboard; set { _s.RestoreClipboard = value; OnPropertyChanged(); } }

    /// <summary>Default: text is inserted while you speak. The alternative buffers and inserts when you stop.</summary>
    public bool InsertProgressively
    {
        get => !_s.InsertWhenFinished;
        set { _s.InsertWhenFinished = !value; OnPropertyChanged(); OnPropertyChanged(nameof(InsertWhenFinished)); }
    }

    public bool InsertWhenFinished
    {
        get => _s.InsertWhenFinished;
        set { _s.InsertWhenFinished = value; OnPropertyChanged(); OnPropertyChanged(nameof(InsertProgressively)); }
    }

    /// <summary>Rambler's mandatory formatting/progressive instructions, appended after the editable prompt.</summary>
    public string ManagedPromptText { get; } = CleanupPromptBuilder.ManagedInstructions(progressive: true);
    public bool PlaySounds { get => _s.PlaySounds; set { _s.PlaySounds = value; OnPropertyChanged(); } }
    public string HotkeyToggle { get => _hotkeyToggle; set => Set(ref _hotkeyToggle, value); }
    public string HotkeyBypass { get => _hotkeyBypass; set => Set(ref _hotkeyBypass, value); }
    public string? ToggleStatus { get => _toggleStatus; private set => Set(ref _toggleStatus, value); }
    public bool ToggleStatusIsError { get => _toggleStatusIsError; private set => Set(ref _toggleStatusIsError, value); }
    public string? BypassStatus { get => _bypassStatus; private set => Set(ref _bypassStatus, value); }
    public bool BypassStatusIsError { get => _bypassStatusIsError; private set => Set(ref _bypassStatusIsError, value); }

    // ---- Audio ----
    public ObservableCollection<Choice<string?>> Devices { get; }
    public Choice<string?> SelectedDevice { get; set; }
    public double MicLevel { get => _micLevel; private set => Set(ref _micLevel, value); }
    public string? MicTestStatus { get => _micTestStatus; private set => Set(ref _micTestStatus, value); }
    public bool AutoStopOnSilence { get => _s.AutoStopOnSilence; set { _s.AutoStopOnSilence = value; OnPropertyChanged(); } }
    public int SilenceTimeoutSeconds { get => _s.SilenceTimeoutSeconds; set { _s.SilenceTimeoutSeconds = value; OnPropertyChanged(); } }
    public int MaxRecordingMinutes { get => _s.MaxRecordingMinutes; set { _s.MaxRecordingMinutes = value; OnPropertyChanged(); } }

    // ---- Gemini ----
    public string? ApiKeyStatus { get => _apiKeyStatus; private set => Set(ref _apiKeyStatus, value); }
    public string PendingApiKey { get => _pendingApiKey; set => Set(ref _pendingApiKey, value); }
    public string LiveModel { get => _s.LiveModel; set { _s.LiveModel = value; OnPropertyChanged(); } }
    public string RecordedModel { get => _s.RecordedModel; set { _s.RecordedModel = value; OnPropertyChanged(); } }
    public bool UseRecordedFallback { get => _s.UseRecordedFallback; set { _s.UseRecordedFallback = value; OnPropertyChanged(); } }
    public string LanguageCode { get => _s.LanguageCode; set { _s.LanguageCode = value; OnPropertyChanged(); } }
    public string VocabularyText { get => _vocabularyText; set => Set(ref _vocabularyText, value); }
    public string? ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string? LastError { get; }
    public bool HasLastError => !string.IsNullOrEmpty(LastError);

    public IReadOnlyList<string> LiveModelSuggestions { get; } = [AppSettings.DefaultLiveModel];
    public IReadOnlyList<string> RecordedModelSuggestions { get; } = [AppSettings.DefaultRecordedModel];

    // ---- Cleanup ----
    public IReadOnlyList<string> CleanupModelSuggestions { get; } =
        ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.7-flash", "gemini-3.8-flash"];
    public string CleanupModel { get => _s.CleanupModel; set { _s.CleanupModel = value; OnPropertyChanged(); } }

    public IReadOnlyList<Choice<string>> ThinkingLevels { get; } =
    [
        new("MINIMAL", "Minimal (fastest)"), new("LOW", "Low"), new("MEDIUM", "Medium"), new("HIGH", "High"),
        new("", "Model default"),
    ];
    public Choice<string> SelectedThinkingLevel
    {
        get => ThinkingLevels.FirstOrDefault(t => t.Value == _s.CleanupThinkingLevel) ?? ThinkingLevels[0];
        set { _s.CleanupThinkingLevel = value.Value; OnPropertyChanged(); }
    }

    public string CleanupPrompt { get => _cleanupPrompt; set => Set(ref _cleanupPrompt, value); }
    public int TechnicalRefinement
    {
        get => _s.TechnicalRefinement;
        set { _s.TechnicalRefinement = value; OnPropertyChanged(); OnPropertyChanged(nameof(RefinementDescription)); }
    }
    public string RefinementDescription => CleanupPromptBuilder.RefinementGuidance(TechnicalRefinement);

    public string? SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }

    // ---- Actions ----

    public void Save()
    {
        // Shortcuts are optional: empty disables one. Non-empty ones must be valid and distinct.
        var toggleText = (HotkeyToggle ?? string.Empty).Trim();
        var bypassText = (HotkeyBypass ?? string.Empty).Trim();
        HotkeyGesture main = default, bypass = default;
        var valid = true;
        if (toggleText.Length > 0 && !HotkeyGesture.TryParse(toggleText, out main))
        {
            SetStatus(HotkeyAction.ToggleDefault, InvalidShortcut, error: true);
            valid = false;
        }
        if (bypassText.Length > 0 && !HotkeyGesture.TryParse(bypassText, out bypass))
        {
            SetStatus(HotkeyAction.ToggleSmartBypass, InvalidShortcut, error: true);
            valid = false;
        }
        if (valid && toggleText.Length > 0 && bypassText.Length > 0 && main == bypass)
        {
            SetStatus(HotkeyAction.ToggleSmartBypass, "Same shortcut as the main one. Record a different one, or clear it.", error: true);
            valid = false;
        }
        if (!valid)
        {
            SaveStatus = "Fix the shortcut on the General tab.";
            return;
        }

        _s.HotkeyToggle = toggleText.Length > 0 ? main.ToString() : string.Empty;
        _s.HotkeySmartBypass = bypassText.Length > 0 ? bypass.ToString() : string.Empty;
        HotkeyToggle = _s.HotkeyToggle;
        HotkeyBypass = _s.HotkeySmartBypass;
        _s.MicrophoneDeviceId = SelectedDevice.Value;
        _s.CustomVocabulary = VocabularyText.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        _s.CleanupPrompt = string.IsNullOrWhiteSpace(CleanupPrompt) ? null : CleanupPrompt;

        try
        {
            if (!string.IsNullOrWhiteSpace(PendingApiKey))
            {
                _credentials.SetApiKey(PendingApiKey.Trim());
                Redactor.RegisterSecret(PendingApiKey.Trim());
                PendingApiKey = string.Empty;
                RefreshApiKeyStatus();
            }
            _settingsService.Save(_s);
            StartupRegistration.Apply(_s.StartWithWindows);
        }
        catch (Exception ex)
        {
            AppLog.Error("Save settings", ex);
            SaveStatus = "Couldn't save: " + ex.Message;
            return;
        }

        _applyTheme(_s.Theme);
        var results = _applyHotkeys(_s.HotkeyToggle, _s.HotkeySmartBypass);
        ShowHotkeyResults(results);
        if (results.Any(r => !r.Success))
        {
            SaveStatus = "Saved, but a shortcut is taken by Windows or another app. Record a different one or clear it (General tab).";
            return;
        }

        CloseRequested?.Invoke();
    }

    private const string InvalidShortcut =
        "Not a valid shortcut. Use at least one of Ctrl, Alt, Shift or Win plus a key (e.g. Ctrl+Alt+D), or leave it empty.";

    private void ShowHotkeyResults(IReadOnlyList<HotkeyRegistration> results)
    {
        foreach (var r in results)
        {
            if (r.IsDisabled) SetStatus(r.Action, "Off. Use the popup's microphone button instead.", error: false);
            else if (r.Success) SetStatus(r.Action, "✓ Active", error: false);
            else SetStatus(r.Action, "✗ " + r.Error, error: true);
        }
    }

    private void SetStatus(HotkeyAction action, string text, bool error)
    {
        if (action == HotkeyAction.ToggleDefault)
        {
            ToggleStatus = text;
            ToggleStatusIsError = error;
        }
        else
        {
            BypassStatus = text;
            BypassStatusIsError = error;
        }
    }

    private void RefreshApiKeyStatus()
    {
        var key = _credentials.GetApiKey();
        ApiKeyStatus = key is null
            ? "No API key saved."
            : _credentials.IsFromEnvironment
                ? "Using the GEMINI_API_KEY environment variable."
                : $"Saved in Windows Credential Manager (…{key[^Math.Min(4, key.Length)..]}).";
    }

    private void RemoveApiKey()
    {
        try
        {
            _credentials.DeleteApiKey();
        }
        catch (Exception ex)
        {
            ApiKeyStatus = ex.Message;
            return;
        }
        RefreshApiKeyStatus();
    }

    private async Task TestConnectionAsync()
    {
        var key = string.IsNullOrWhiteSpace(PendingApiKey) ? _credentials.GetApiKey() : PendingApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ConnectionStatus = "Enter an API key first.";
            return;
        }

        _testingConnection = true;
        ConnectionStatus = "Testing…";
        try
        {
            Redactor.RegisterSecret(key);
            var snapshot = _s.Clone();
            snapshot.CustomVocabulary = VocabularyText.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            snapshot.Normalize();
            var results = await Task.Run(() => _tester.TestAsync(snapshot, key, CancellationToken.None));
            ConnectionStatus = string.Join("\n", results.Select(r => $"{(r.Ok ? "✓" : "✗")} {r.Name}: {r.Detail}"));
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Test failed: " + Redactor.Redact(ex.Message);
        }
        finally
        {
            _testingConnection = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Records ~4 s from the selected microphone, shows the level, then plays it back. Nothing is saved.</summary>
    private async Task TestMicrophoneAsync()
    {
        _micTesting = true;
        using var source = new WasapiAudioSource();
        var recording = new MemoryStream();
        source.DataAvailable += pcm => { lock (recording) recording.Write(pcm); };
        source.LevelChanged += level => _dispatcher.BeginInvoke(() => MicLevel = level);
        try
        {
            source.Start(SelectedDevice.Value);
            MicTestStatus = $"Recording from {source.CurrentDeviceName}… say something.";
            await Task.Delay(TimeSpan.FromSeconds(4));
            source.Stop();

            byte[] wav;
            lock (recording) wav = WavWriter.ToWav(recording.GetBuffer().AsSpan(0, (int)recording.Length));
            MicTestStatus = "Playing back…";
            using (var player = new SoundPlayer(new MemoryStream(wav)))
                await Task.Run(player.PlaySync);
            Array.Clear(wav);
            MicTestStatus = source.UsedDefaultFallback
                ? "Done. The selected microphone wasn't available, so the default was used."
                : "Done. If you heard yourself, the microphone works.";
        }
        catch (AudioDeviceException ex)
        {
            MicTestStatus = ex.Message;
        }
        catch (Exception ex)
        {
            MicTestStatus = "Microphone test failed: " + ex.Message;
        }
        finally
        {
            source.Stop();
            Array.Clear(recording.GetBuffer());
            recording.Dispose();
            MicLevel = 0;
            _micTesting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
