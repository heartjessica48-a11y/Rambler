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
    private string? _hotkeyStatus;
    private bool _hotkeyStatusIsError;
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
        string? lastError, Dispatcher dispatcher)
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
        new(InsertionMethod.Auto, "Automatic (type short text, paste long or multi-line text)"),
        new(InsertionMethod.Type, "Always type"),
        new(InsertionMethod.Paste, "Always paste via clipboard"),
    ];
    public Choice<InsertionMethod> SelectedInsertionMethod
    {
        get => InsertionMethods.First(m => m.Value == _s.InsertionMethod);
        set { _s.InsertionMethod = value.Value; OnPropertyChanged(); }
    }

    public bool RestoreClipboard { get => _s.RestoreClipboard; set { _s.RestoreClipboard = value; OnPropertyChanged(); } }
    public bool PlaySounds { get => _s.PlaySounds; set { _s.PlaySounds = value; OnPropertyChanged(); } }
    public string HotkeyToggle { get => _hotkeyToggle; set => Set(ref _hotkeyToggle, value); }
    public string HotkeyBypass { get => _hotkeyBypass; set => Set(ref _hotkeyBypass, value); }
    public string? HotkeyStatus { get => _hotkeyStatus; private set => Set(ref _hotkeyStatus, value); }
    public bool HotkeyStatusIsError { get => _hotkeyStatusIsError; private set => Set(ref _hotkeyStatusIsError, value); }

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
        if (!HotkeyGesture.TryParse(HotkeyToggle, out var main) || !HotkeyGesture.TryParse(HotkeyBypass, out var bypass))
        {
            SetHotkeyStatus("Enter shortcuts like Ctrl+Win+Space (at least one modifier).", error: true);
            SaveStatus = "Fix the shortcut on the General tab.";
            return;
        }
        if (main == bypass)
        {
            SetHotkeyStatus("The two shortcuts must be different.", error: true);
            SaveStatus = "Fix the shortcut on the General tab.";
            return;
        }

        _s.HotkeyToggle = main.ToString();
        _s.HotkeySmartBypass = bypass.ToString();
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
        var failures = _applyHotkeys(_s.HotkeyToggle, _s.HotkeySmartBypass).Where(r => !r.Success).ToList();
        if (failures.Count > 0)
        {
            SetHotkeyStatus(string.Join("\n", failures.Select(f => f.Error)), error: true);
            SaveStatus = "Saved, but a shortcut couldn't be registered. Choose another on the General tab.";
            return;
        }

        CloseRequested?.Invoke();
    }

    private void SetHotkeyStatus(string? text, bool error)
    {
        HotkeyStatus = text;
        HotkeyStatusIsError = error;
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
