using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Rambler.Core;
using Rambler.Core.Dictation;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

/// <summary>
/// Identifies and tracks the text field dictation is meant for.
/// <list type="bullet">
/// <item>Window: the foreground top-level window (or, if Rambler's popup is in front, the window the user
/// was in before opening it).</item>
/// <item>Control: the thread's focused HWND (GetGUIThreadInfo) and, when available, the UI Automation focused
/// element's runtime id, because browsers and Electron apps use a single HWND for every field.</item>
/// <item>Editability: UI Automation control type / ValuePattern, plus window-class heuristics (desktop,
/// classic edit controls) when UI Automation can't answer.</item>
/// </list>
/// Read-only Win32 and UI Automation queries only: no hooks into other processes, no injection.
/// </summary>
public sealed class TargetWindowService : IDisposable
{
    private static readonly TimeSpan s_uiaTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly HashSet<string> s_desktopClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>Classic editors where Enter inserts a line break (never "sends").</summary>
    private static readonly HashSet<string> s_plainEditorClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RichEdit20W", "RichEdit20A", "RICHEDIT50W", "RichEditD2DPT", "Scintilla", "_WwG",
    };

    private static readonly ControlType[] s_nonTextControls =
    [
        ControlType.List, ControlType.ListItem, ControlType.Tree, ControlType.TreeItem, ControlType.Button,
        ControlType.MenuItem, ControlType.Menu, ControlType.MenuBar, ControlType.TabItem, ControlType.CheckBox,
        ControlType.RadioButton, ControlType.Image, ControlType.Hyperlink, ControlType.DataItem, ControlType.Header,
        ControlType.HeaderItem, ControlType.TitleBar, ControlType.ScrollBar, ControlType.Slider,
    ];

    private readonly int _ownPid = Environment.ProcessId;
    private readonly WinEventProc _foregroundProc;
    private readonly nint _hook;
    private nint _lastExternalWindow;

    public TargetWindowService()
    {
        _foregroundProc = OnForegroundChanged; // keep the delegate alive
        _lastExternalWindow = GetForegroundWindow();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Raised when another application's window comes to the foreground.</summary>
    public event Action? ForegroundChanged;

    public bool IsOwnWindowActive => WindowPid(GetForegroundWindow()) == _ownPid;

    /// <summary>Captures the intended target: window, focused control and whether it accepts text.</summary>
    public InsertionTarget Capture()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || WindowPid(hwnd) == _ownPid) hwnd = _lastExternalWindow;
        if (hwnd == 0 || !IsWindow(hwnd)) return InsertionTarget.None;

        var root = Root(hwnd);
        var thread = GetWindowThreadProcessId(root, out var pid);
        var focus = FocusedHandle(thread);
        var rootClass = ClassName(root);

        if (s_desktopClasses.Contains(rootClass))
            return new InsertionTarget(root, (int)pid, "Desktop") { AppName = "Desktop", Kind = TargetKind.NotEditable, FocusHandle = focus };

        var appName = AppName(pid);
        var focusClass = focus != 0 ? ClassName(focus) : string.Empty;
        // UI Automation only describes the foreground app. When Rambler's popup is in front, its focused
        // element is ours (and querying it from the UI thread would just time out): use Win32 info instead.
        var targetInFront = Root(GetForegroundWindow()) == root;
        var (kind, key) = targetInFront
            ? InspectFocus((int)pid, focusClass)
            : (s_plainEditorClasses.Contains(focusClass) ? TargetKind.Editable : TargetKind.Unknown, (string?)null);
        return new InsertionTarget(root, (int)pid, appName)
        {
            AppName = appName,
            Kind = kind,
            FocusHandle = focus,
            ElementKey = key,
        };
    }

    /// <summary>
    /// True when the target window is in the foreground and the same control still has focus.
    /// <paramref name="thorough"/> also compares the UI Automation element (slower; skipped between typing batches).
    /// </summary>
    public bool IsReady(InsertionTarget target, bool thorough = true)
    {
        if (target.IsNone || !IsWindow(target.WindowHandle)) return false;
        var fg = GetForegroundWindow();
        if (fg == 0 || Root(fg) != target.WindowHandle) return false;
        if (WindowPid(target.WindowHandle) != target.ProcessId) return false;

        if (target.FocusHandle != 0)
        {
            var thread = GetWindowThreadProcessId(target.WindowHandle, out _);
            var focus = FocusedHandle(thread);
            if (focus != 0 && focus != target.FocusHandle) return false;
        }

        if (thorough && target.ElementKey is not null)
        {
            var (pid, key) = FocusedElementKey();
            // A different field in the same app: pause instead of guessing. Unknown (UIA unavailable): allow.
            if (key is not null && pid == target.ProcessId && key != target.ElementKey) return false;
        }
        return true;
    }

    /// <summary>The focused control is a classic editor where Enter inserts a line break.</summary>
    public bool IsPlainEditor(InsertionTarget target) =>
        target.FocusHandle != 0 && s_plainEditorClasses.Contains(ClassName(target.FocusHandle));

    // ---- Helpers ---------------------------------------------------------------------------------

    private static (TargetKind Kind, string? Key) InspectFocus(int pid, string focusClass)
    {
        var fallback = s_plainEditorClasses.Contains(focusClass) ? TargetKind.Editable : TargetKind.Unknown;
        var info = RunUia(() =>
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return ((TargetKind, string?, int)?)null;
            var current = element.Current;
            var key = string.Join(".", element.GetRuntimeId());

            TargetKind kind;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern value)
                kind = value.Current.IsReadOnly ? TargetKind.NotEditable : TargetKind.Editable;
            else if (current.ControlType == ControlType.Edit)
                kind = TargetKind.Editable;
            else if (current.ControlType == ControlType.Document)
                kind = TargetKind.Unknown; // rich editors and terminals: allowed
            else if (s_nonTextControls.Contains(current.ControlType))
                kind = TargetKind.NotEditable;
            else
                kind = TargetKind.Unknown;
            return (kind, key, current.ProcessId);
        });

        // Only trust UI Automation when it describes the target app (not Rambler's own popup).
        if (info is { } i && i.Item3 == pid) return (i.Item1, i.Item2);
        return (fallback, null);
    }

    private static (int Pid, string? Key) FocusedElementKey()
    {
        var info = RunUia(() =>
        {
            var element = AutomationElement.FocusedElement;
            return element is null ? ((int, string)?)null : (element.Current.ProcessId, string.Join(".", element.GetRuntimeId()));
        });
        return info is { } i ? (i.Item1, i.Item2) : (0, null);
    }

    /// <summary>UI Automation calls are cross-process and can hang on a busy app: bound them with a timeout.</summary>
    private static T? RunUia<T>(Func<T?> query) where T : struct
    {
        try
        {
            var task = Task.Run(() =>
            {
                try { return query(); }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                {
                    return null;
                }
            });
            return task.Wait(s_uiaTimeout) ? task.Result : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("UI Automation query failed: " + ex.Message);
            return null;
        }
    }

    private static nint FocusedHandle(uint thread)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : 0;
    }

    private static nint Root(nint hwnd) => GetAncestor(hwnd, GA_ROOT) is var root && root != 0 ? root : hwnd;

    private static int WindowPid(nint hwnd)
    {
        if (hwnd == 0) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>Friendly name ("Discord", "Notepad", "Google Chrome") from the executable's version info.</summary>
    private static string AppName(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return "the target app";
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(process, 0, sb, ref size)) return "the target app";
            var path = sb.ToString();
            var exe = Path.GetFileNameWithoutExtension(path);
            if (exe.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 40) return name.Trim();
            }
            catch (Exception)
            {
                // fall back to the executable name
            }
            return exe;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private void OnForegroundChanged(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != 0 && WindowPid(hwnd) != _ownPid) _lastExternalWindow = hwnd;
        ForegroundChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
    }
}
