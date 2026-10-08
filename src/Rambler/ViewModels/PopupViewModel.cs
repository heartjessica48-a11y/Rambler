using System.Windows.Input;
using System.Windows.Threading;
using Rambler.Core.Dictation;
using Rambler.Core.Settings;

namespace Rambler.ViewModels;

public sealed class PopupViewModel : ObservableObject
{
    private readonly DictationCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly Func<bool> _hasApiKey;
    private readonly Dispatcher _dispatcher;
    private int _refreshQueued;

    private DictationState _state;
    private bool _isCleanupMode;
    private string _preview = string.Empty;
    private double _level;
    private string? _message;
    private bool _messageIsError;
    private bool _hasPending;
    private bool _hasLastResult;
    private string _hotkeyText = string.Empty;
    private string _bypassHotkeyText = string.Empty;
    private string _targetText = string.Empty;
    private bool _needsApiKey;

    public PopupViewModel(DictationCoordinator coordinator, SettingsService settings, Func<bool> hasApiKey,
        Action openSettings, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _settings = settings;
        _hasApiKey = hasApiKey;
        _dispatcher = dispatcher;

        ToggleCommand = new AsyncCommand(() => _coordinator.ToggleAsync(DictationTrigger.Default),
            () => !_coordinator.State.IsBusy());
        CancelCommand = new AsyncCommand(_coordinator.CancelAsync);
        InsertPendingCommand = new AsyncCommand(_coordinator.InsertPendingAsync, () => HasPending);
        CopyPendingCommand = new RelayCommand(() => _coordinator.CopyPending(), () => HasPending);
        CopyLastCommand = new RelayCommand(() => _coordinator.CopyLastResult(), () => HasLastResult);
        DismissCommand = new RelayCommand(_coordinator.Dismiss);
        OpenSettingsCommand = new RelayCommand(openSettings);

        _coordinator.Changed += QueueRefresh;
        _coordinator.LevelChanged += level => _dispatcher.BeginInvoke(() => Level = level, DispatcherPriority.Render);
        _settings.Changed += _ => QueueRefresh();
        Refresh();
    }

    public ICommand ToggleCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand InsertPendingCommand { get; }
    public ICommand CopyPendingCommand { get; }
    public ICommand CopyLastCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public DictationState State { get => _state; private set { if (Set(ref _state, value)) OnStateDependentsChanged(); } }

    public bool IsListening => State == DictationState.Listening;
    public bool IsBusy => State.IsBusy();
    public bool CanCancel => State is DictationState.Listening or DictationState.Finalizing or DictationState.Cleaning;

    public string StatusText => State switch
    {
        DictationState.Idle => "Ready",
        DictationState.Listening => _coordinator.ActiveMode == DictationMode.SmartOnly ? "Listening · Smart Only" : "Listening · Cleanup",
        DictationState.Finalizing => "Finalizing…",
        DictationState.Cleaning => "Cleaning up…",
        DictationState.Inserting => "Inserting…",
        DictationState.Error => "Needs attention",
        _ => string.Empty,
    };

    public string MicButtonText => State switch
    {
        DictationState.Listening => "Stop",
        DictationState.Finalizing or DictationState.Cleaning or DictationState.Inserting => "Working…",
        _ => "Start",
    };

    /// <summary>Segmented control: true = Prompt Cleanup, false = Smart Only. Saved immediately.</summary>
    public bool IsCleanupMode
    {
        get => _isCleanupMode;
        set
        {
            if (!Set(ref _isCleanupMode, value)) return;
            OnPropertyChanged(nameof(IsSmartMode));
            var mode = value ? DictationMode.PromptCleanup : DictationMode.SmartOnly;
            var s = _settings.Current;
            if (s.DefaultMode != mode)
            {
                s.DefaultMode = mode;
                _settings.Save(s);
            }
            _coordinator.ChangeMode(mode);
        }
    }

    public bool IsSmartMode
    {
        get => !_isCleanupMode;
        set => IsCleanupMode = !value;
    }

    public string Preview { get => _preview; private set { if (Set(ref _preview, value)) OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => Preview.Length > 0;
    public double Level { get => _level; private set => Set(ref _level, value); }
    public string? Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }
    public bool HasPending { get => _hasPending; private set => Set(ref _hasPending, value); }
    public bool HasLastResult { get => _hasLastResult; private set => Set(ref _hasLastResult, value); }
    public string HotkeyText
    {
        get => _hotkeyText;
        private set { if (Set(ref _hotkeyText, value)) OnPropertyChanged(nameof(HasHotkey)); }
    }
    public string BypassHotkeyText
    {
        get => _bypassHotkeyText;
        private set { if (Set(ref _bypassHotkeyText, value)) OnPropertyChanged(nameof(HasBypassHotkey)); }
    }
    public bool HasHotkey => HotkeyText.Length > 0;
    public bool HasBypassHotkey => BypassHotkeyText.Length > 0;
    public string TargetText { get => _targetText; private set => Set(ref _targetText, value); }
    public bool NeedsApiKey { get => _needsApiKey; private set => Set(ref _needsApiKey, value); }

    /// <summary>Coalesces bursts of coordinator events into one UI update.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }, DispatcherPriority.DataBind);
    }

    public void Refresh()
    {
        var settings = _settings.Current;
        State = _coordinator.State;
        Preview = _coordinator.Preview;
        Message = _coordinator.Message;
        MessageIsError = _coordinator.MessageIsError;
        HasPending = _coordinator.PendingText is not null;
        HasLastResult = _coordinator.LastResult is not null;
        HotkeyText = settings.HotkeyToggle;
        BypassHotkeyText = settings.HotkeySmartBypass;
        NeedsApiKey = !_hasApiKey();
        TargetText = State is DictationState.Listening or DictationState.Finalizing or DictationState.Cleaning
            ? "Into: " + _coordinator.TargetDescription
            : string.Empty;

        if (_isCleanupMode != (settings.DefaultMode == DictationMode.PromptCleanup))
        {
            _isCleanupMode = settings.DefaultMode == DictationMode.PromptCleanup;
            OnPropertyChanged(nameof(IsCleanupMode));
            OnPropertyChanged(nameof(IsSmartMode));
        }
        OnPropertyChanged(nameof(StatusText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnStateDependentsChanged()
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MicButtonText));
    }
}

internal static class DictationStateExtensions
{
    public static bool IsBusy(this DictationState state) =>
        state is DictationState.Finalizing or DictationState.Cleaning or DictationState.Inserting;
}
