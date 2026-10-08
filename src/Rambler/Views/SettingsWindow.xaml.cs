using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rambler.Core;
using Rambler.Core.Settings;
using Rambler.ViewModels;

namespace Rambler.Views;

public partial class SettingsWindow : Window
{
    private const string RecordPrompt = "Press keys… (Esc cancels)";

    private readonly SettingsViewModel _viewModel;
    private readonly Action _suspendHotkeys;
    private readonly Action _resumeHotkeys;
    private TextBox? _recordingBox;
    private Button? _recordingButton;
    private string _textBeforeRecording = string.Empty;

    public SettingsWindow(SettingsViewModel viewModel, Action suspendHotkeys, Action resumeHotkeys, bool focusGeminiTab)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _suspendHotkeys = suspendHotkeys;
        _resumeHotkeys = resumeHotkeys;
        DataContext = viewModel;

        viewModel.CloseRequested += Close;
        ApiKeyBox.PasswordChanged += (_, _) => viewModel.PendingApiKey = ApiKeyBox.Password;
        LogPathText.Text = AppLog.FilePath is { } path ? "Log file: " + path : string.Empty;

        foreach (var box in new[] { HotkeyToggleBox, HotkeyBypassBox })
        {
            box.PreviewKeyDown += OnHotkeyKeyDown;
            box.PreviewKeyUp += OnHotkeyKeyUp;
            // Release Rambler's own hotkeys while editing, so pressing the current shortcut can be captured.
            box.GotKeyboardFocus += (_, _) => _suspendHotkeys();
            box.LostKeyboardFocus += (_, e) =>
            {
                if (_recordingBox == box && !ReferenceEquals(e.NewFocus, _recordingButton)) StopRecording(commit: false);
                _resumeHotkeys();
            };
        }

        if (focusGeminiTab) Tabs.SelectedIndex = 2;
        Closed += (_, _) => _resumeHotkeys();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        StopRecording(commit: false);
        // Commit editable combo boxes that update on LostFocus.
        if (Keyboard.FocusedElement is FrameworkElement fe) fe.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        _viewModel.Save();
    }

    // ---- Shortcut recording ------------------------------------------------------------------

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var box = (TextBox)button.Tag;
        if (_recordingBox == box)
        {
            StopRecording(commit: false); // the button reads "Cancel" while recording
            return;
        }

        StopRecording(commit: false);
        _recordingBox = box;
        _recordingButton = button;
        _textBeforeRecording = box.Text;
        box.IsReadOnly = true;
        box.Text = RecordPrompt;
        button.Content = "Cancel";
        box.Focus();
        _suspendHotkeys();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        StopRecording(commit: false);
        ((TextBox)((Button)sender).Tag).Text = string.Empty; // empty = shortcut off
    }

    private void StopRecording(bool commit)
    {
        if (_recordingBox is null) return;
        if (!commit) _recordingBox.Text = _textBeforeRecording;
        _recordingBox.IsReadOnly = false;
        _recordingBox.CaretIndex = _recordingBox.Text.Length;
        if (_recordingButton is not null) _recordingButton.Content = "Record";
        _recordingBox = null;
        _recordingButton = null;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = CurrentModifiers();

        if (_recordingBox == box)
        {
            e.Handled = true;
            if (modifiers == HotkeyModifiers.None && key == Key.Escape) { StopRecording(commit: false); return; }
            if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete)
            {
                box.Text = string.Empty; // turn the shortcut off
                StopRecording(commit: true);
                return;
            }
            if (IsModifierKey(key))
            {
                box.Text = Describe(modifiers) + "…";
                return;
            }

            var gesture = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
            if (gesture.IsValid)
            {
                box.Text = gesture.ToString();
                StopRecording(commit: true);
            }
            else
            {
                box.Text = "Add Ctrl, Alt, Shift or Win… (Esc cancels)";
            }
            return;
        }

        // Not recording: a pressed shortcut with modifiers is captured directly; plain typing is left alone.
        if (IsModifierKey(key) || key == Key.Tab) return;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if ((modifiers & ~HotkeyModifiers.Shift) == HotkeyModifiers.None && !isFunctionKey) return;
        var direct = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (!direct.IsValid) return;
        box.Text = direct.ToString();
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        if (_recordingBox != sender) return;
        e.Handled = true;
        var modifiers = CurrentModifiers();
        _recordingBox.Text = modifiers == HotkeyModifiers.None ? RecordPrompt : Describe(modifiers) + "…";
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        var wpf = Keyboard.Modifiers;
        if (wpf.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (wpf.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (wpf.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;

    private static string Describe(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        return string.Join("+", parts) + "+";
    }
}
