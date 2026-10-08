using System.Drawing;
using System.Drawing.Drawing2D;
using Rambler.Core.Dictation;
using Forms = System.Windows.Forms;

namespace Rambler.Services;

/// <summary>System tray icon (WinForms NotifyIcon) with one icon per dictation state.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<DictationState, Icon> _icons;

    public TrayService()
    {
        _icons = new Dictionary<DictationState, Icon>
        {
            [DictationState.Idle] = CreateIcon(Color.FromArgb(37, 99, 235), error: false),
            [DictationState.Listening] = CreateIcon(Color.FromArgb(220, 38, 38), error: false),
            [DictationState.Finalizing] = CreateIcon(Color.FromArgb(217, 119, 6), error: false),
            [DictationState.Error] = CreateIcon(Color.FromArgb(107, 114, 128), error: true),
        };
        _icons[DictationState.Cleaning] = _icons[DictationState.Finalizing];
        _icons[DictationState.Inserting] = _icons[DictationState.Finalizing];

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Restart", null, (_, _) => RestartRequested?.Invoke());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = _icons[DictationState.Idle],
            Text = "Rambler: Ready",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) LeftClicked?.Invoke(Forms.Cursor.Position);
        };
    }

    /// <summary>Left click on the icon, with the cursor position in physical screen pixels.</summary>
    public event Action<Point>? LeftClicked;
    public event Action? SettingsRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    public void SetState(DictationState state, string tooltip)
    {
        _icon.Icon = _icons[state];
        var text = "Rambler: " + tooltip;
        _icon.Text = text.Length > 120 ? text[..120] + "…" : text;
    }

    public void ShowNotification(string message, bool isError)
    {
        _icon.ShowBalloonTip(4000, "Rambler", message, isError ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>Draws a rounded tile with a microphone (or "!") so the state is readable at 16 px.</summary>
    private static Icon CreateIcon(Color background, bool error)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(background);
            using var tile = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), 7);
            g.FillPath(bg, tile);

            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (error)
            {
                g.FillRectangle(white, 14.5f, 7, 3, 12);
                g.FillEllipse(white, 14, 22, 4, 4);
            }
            else
            {
                using var capsule = RoundedRect(new RectangleF(12, 5, 8, 14), 4);
                g.FillPath(white, capsule);
                g.DrawArc(pen, 8.5f, 9.5f, 15, 13, 0, 180);
                g.DrawLine(pen, 16, 23, 16, 26.5f);
                g.DrawLine(pen, 12, 27, 20, 27);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // own a copy so the GDI handle can be destroyed
        }
        finally
        {
            Interop.NativeMethods.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values.Distinct()) icon.Dispose();
    }
}
