using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Rambler.Core;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Transcription;
using Rambler.Services;
using Rambler.ViewModels;
using Rambler.Views;

namespace Rambler;

/// <summary>
/// AppHost: composes the services, owns the tray lifecycle, and keeps tray + popup in sync with dictation state.
/// The app has no taskbar window and starts in the tray; it never records until the user starts dictation.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Rambler.SingleInstance";
    private const string ActivateEventName = @"Local\Rambler.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private SettingsService _settings = null!;
    private WindowsCredentialStore _credentials = null!;
    private HttpClient _http = null!;
    private GeminiHttp _gemini = null!;
    private ThemeService _theme = null!;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private IReadOnlyList<HotkeyRegistration> _hotkeyResults = [];
    private WasapiAudioSource? _audio;
    private TextInsertionService? _inserter;
    private DictationCoordinator? _coordinator;
    private PopupViewModel _popupViewModel = null!;
    private PopupWindow? _popup;
    private FeedbackSounds? _sounds;
    private SettingsWindow? _settingsWindow;
    private DictationState _lastState;
    private string? _lastError;
    private bool _hasApiKey;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstance(e.Args.Contains("--restart")))
        {
            Shutdown();
            return;
        }

        AppLog.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rambler", "logs", "rambler.log"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Error("Unhandled", ex);
        };

        _settings = new SettingsService(SettingsService.DefaultPath);
        _credentials = new WindowsCredentialStore();
        var key = _credentials.GetApiKey();
        _hasApiKey = !string.IsNullOrWhiteSpace(key);
        Redactor.RegisterSecret(key);

        _theme = new ThemeService();
        _theme.Apply(_settings.Current.Theme);

        _http = GeminiHttp.CreateDefaultClient();
        _gemini = new GeminiHttp(_http);
        _audio = new WasapiAudioSource();
        _inserter = new TextInsertionService(() => _settings.Current, HidePopupAsync);
        var sessions = new LiveTranscriptionSessionFactory(new ClientWebSocketFactory(), new GeminiRecordedTranscriber(_gemini));
        _coordinator = new DictationCoordinator(_audio, sessions, new GeminiCleanupService(_gemini), _inserter,
            () => _settings.Current, () => _credentials.GetApiKey());
        _coordinator.Changed += () => Dispatcher.BeginInvoke(SyncWithState);
        _coordinator.Notify += (message, isError) => Dispatcher.BeginInvoke(() =>
        {
            if (_popup is not { IsVisible: true }) _tray?.ShowNotification(message, isError);
        });

        _popupViewModel = new PopupViewModel(_coordinator, _settings, () => _hasApiKey, () => OpenSettings(false), Dispatcher);
        _popup = new PopupWindow(_popupViewModel);
        _sounds = new FeedbackSounds();

        System.Windows.Forms.Application.EnableVisualStyles();
        _tray = new TrayService();
        _tray.LeftClicked += position => _popup.Toggle(position);
        _tray.SettingsRequested += () => OpenSettings(false);
        _tray.RestartRequested += Restart;
        _tray.ExitRequested += () => ExitApp();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys(_settings.Current.HotkeyToggle, _settings.Current.HotkeySmartBypass, notify: true);

        if (_settings.LoadWarning is { } warning) _tray.ShowNotification(warning, isError: true);
        if (!_hasApiKey)
        {
            // First run: ask for the key. When launched at sign-in, don't pop a window; just leave a hint.
            if (e.Args.Contains("--startup")) _tray.ShowNotification("Add your Gemini API key in Settings to start dictating.", isError: false);
            else OpenSettings(focusGemini: true);
        }
    }

    // ---- Hotkeys & state ----------------------------------------------------------------------

    private void OnHotkey(HotkeyAction action)
    {
        if (_coordinator is null) return;
        var trigger = action == HotkeyAction.ToggleSmartBypass ? DictationTrigger.SmartBypass : DictationTrigger.Default;
        _ = _coordinator.ToggleAsync(trigger);
    }

    private IReadOnlyList<HotkeyRegistration> ApplyHotkeys(string toggle, string bypass, bool notify)
    {
        var results = _hotkeys!.Apply(toggle, bypass);
        _hotkeyResults = results;
        var failed = results.Where(r => !r.Success).ToList();
        if (notify && failed.Count > 0)
            _tray?.ShowNotification("Shortcut conflict: " + failed[0].Error + " Change it in Settings.", isError: true);
        return results;
    }

    private void SyncWithState()
    {
        if (_coordinator is null || _tray is null) return;
        var state = _coordinator.State;
        var tooltip = state switch
        {
            DictationState.Idle => "Ready",
            DictationState.Listening => "Listening (microphone on)",
            DictationState.Finalizing => "Finalizing transcript",
            DictationState.Cleaning => "Cleaning up",
            DictationState.Inserting => "Inserting text",
            _ => _coordinator.Message ?? "Error",
        };
        _tray.SetState(state, tooltip);

        if (state == DictationState.Error && _coordinator.MessageIsError) _lastError = _coordinator.Message;

        if (state != _lastState && _settings.Current.PlaySounds)
        {
            if (state == DictationState.Listening) _sounds?.PlayStart();
            else if (_lastState == DictationState.Listening) _sounds?.PlayStop();
        }
        _lastState = state;
    }

    private Task HidePopupAsync() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_popup is { IsVisible: true }) _popup.Hide();
        }).Task;

    // ---- Settings -----------------------------------------------------------------------------

    private void OpenSettings(bool focusGemini)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _popup?.Hide();
        var viewModel = new SettingsViewModel(_settings, _credentials,
            new GeminiConnectionTester(_gemini, new ClientWebSocketFactory()),
            (toggle, bypass) => ApplyHotkeys(toggle, bypass, notify: false),
            _theme.Apply, _hotkeyResults, _lastError, Dispatcher);

        _settingsWindow = new SettingsWindow(viewModel, () => _hotkeys?.Suspend(), () => _hotkeys?.Resume(), focusGemini);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            var key = _credentials.GetApiKey();
            _hasApiKey = !string.IsNullOrWhiteSpace(key);
            Redactor.RegisterSecret(key);
            _popupViewModel.Refresh();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- Lifecycle ----------------------------------------------------------------------------

    private bool AcquireSingleInstance(bool isRestart)
    {
        _singleInstance = new Mutex(true, MutexName, out var owned);
        if (!owned && isRestart)
        {
            try { owned = _singleInstance.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
        }

        if (!owned)
        {
            // Already running: ask that instance to show its popup.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _popup?.ShowNearTray()), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            try { Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false }); }
            catch (Exception ex) { AppLog.Error("Restart", ex); }
        }
        ExitApp();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        // Release the microphone and network first; never leave capture running.
        _audio?.Stop();
        try
        {
            _coordinator?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            AppLog.Error("Shutdown", ex);
        }

        _settingsWindow?.Close();
        _popup?.ForceClose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _inserter?.Dispose();
        _audio?.Dispose();
        _sounds?.Dispose();
        _theme?.Dispose();
        _http?.Dispose();
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApp();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _audio?.Stop(); // belt and braces: the microphone is closed on every exit path
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("UI", e.Exception);
        e.Handled = true; // stay alive in the tray
        _tray?.ShowNotification("Something went wrong: " + Redactor.Redact(e.Exception.Message), isError: true);
    }
}
