using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Rambler.ViewModels;
using static Rambler.Interop.NativeMethods;
using Forms = System.Windows.Forms;

namespace Rambler.Views;

/// <summary>Compact tray popup. Dismissed by clicking elsewhere or Esc; hiding it never stops dictation.</summary>
public partial class PopupWindow : Window
{
    private long _lastAutoHideTick;
    private bool _allowClose;
    private System.Drawing.Point? _anchor; // last tray click, in physical screen pixels

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
        // The popup grows with its content; keep it anchored to the tray.
        SizeChanged += (_, _) =>
        {
            if (IsVisible) Reposition();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
    }

    /// <summary>Tray left-click: show or hide. Ignores the click that just dismissed the popup.</summary>
    public void Toggle(System.Drawing.Point clickPosition)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (Environment.TickCount64 - _lastAutoHideTick < 300) return;
        ShowNearTray(clickPosition);
    }

    /// <summary>Shows the popup next to the tray icon (the last click position, if known).</summary>
    public void ShowNearTray(System.Drawing.Point? clickPosition = null)
    {
        if (clickPosition is not null) _anchor = clickPosition;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            Reposition();
            Opacity = 1;
        }
        Activate();
        MicButton.Focus();
    }

    /// <summary>
    /// Places the popup against the taskbar, centered on the tray icon, on whichever monitor and
    /// taskbar edge the icon is on. Works in physical pixels so mixed-DPI setups line up.
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        var screen = _anchor is { } a ? Forms.Screen.FromPoint(a) : Forms.Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        int x, y;

        if (_anchor is not { } p)
        {
            x = work.Right - w;
            y = work.Bottom - h;
        }
        else if (work.Bottom < bounds.Bottom) // taskbar at the bottom
        {
            x = p.X - w / 2;
            y = work.Bottom - h;
        }
        else if (work.Top > bounds.Top) // top
        {
            x = p.X - w / 2;
            y = work.Top;
        }
        else if (work.Left > bounds.Left) // left
        {
            x = work.Left;
            y = p.Y - h / 2;
        }
        else if (work.Right < bounds.Right) // right
        {
            x = work.Right - w;
            y = p.Y - h / 2;
        }
        else // auto-hidden taskbar: open just above the click
        {
            x = p.X - w / 2;
            y = p.Y - h;
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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
