using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rambler.Core;
using Rambler.Core.Settings;
using Rambler.ViewModels;

namespace Rambler.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action _suspendHotkeys;
    private readonly Action _resumeHotkeys;

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
            box.GotKeyboardFocus += (_, _) => _suspendHotkeys();
            box.LostKeyboardFocus += (_, _) => _resumeHotkeys();
        }

        if (focusGeminiTab) Tabs.SelectedIndex = 2;
        Closed += (_, _) => _resumeHotkeys();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Commit editable combo boxes that update on LostFocus.
        if (Keyboard.FocusedElement is FrameworkElement fe) fe.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        _viewModel.Save();
    }

    /// <summary>Captures a pressed shortcut (with modifiers) into the box; plain typing is left alone.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        var wpf = Keyboard.Modifiers;
        if (wpf.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (wpf.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (wpf.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= HotkeyModifiers.Win;

        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if ((modifiers & ~HotkeyModifiers.Shift) == HotkeyModifiers.None && !isFunctionKey) return; // normal typing

        var gesture = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (!gesture.IsValid) return;
        ((TextBox)sender).Text = gesture.ToString();
        ((TextBox)sender).CaretIndex = ((TextBox)sender).Text.Length;
        e.Handled = true;
    }
}
