using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Rambler.ViewModels;

namespace Rambler.Views;

/// <summary>Compact tray popup. Dismissed by clicking elsewhere or Esc; hiding it never stops dictation.</summary>
public partial class PopupWindow : Window
{
    private long _lastAutoHideTick;
    private bool _allowClose;

    public PopupWindow(PopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        Deactivated += (_, _) =>
        {
            if (!IsVisible) return;
            _lastAutoHideTick = Environment.TickCount64;
            Hide();
        };
        // The popup grows with its content; keep its bottom edge anchored above the taskbar.
        SizeChanged += (_, _) =>
        {
            if (!IsVisible) return;
            var area = SystemParameters.WorkArea;
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
    }

    /// <summary>Tray left-click: show or hide. Ignores the click that just dismissed the popup.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (Environment.TickCount64 - _lastAutoHideTick < 300) return;
        ShowNearTray();
    }

    public void ShowNearTray()
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            Opacity = 1;
        }
        Activate();
        MicButton.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 only hides the popup; the app keeps running in the tray.
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PopupViewModel.Preview))
            Dispatcher.BeginInvoke(PreviewScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
