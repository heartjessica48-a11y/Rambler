using System.Runtime.InteropServices;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

/// <summary>
/// Win32 clipboard access with a best-effort snapshot/restore of the user's previous content.
/// Only memory (HGLOBAL) formats are copied; content that can't be copied faithfully
/// (metafiles, private/GDI handles, very large data) makes the snapshot non-restorable,
/// in which case it is left untouched rather than restored incorrectly.
/// </summary>
internal static class ClipboardHelper
{
    private const long MaxSnapshotBytes = 64L * 1024 * 1024;
    private static readonly uint s_excludeFromHistory = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    public sealed class Snapshot
    {
        public List<(uint Format, byte[] Data)> Items { get; } = [];
        public bool Restorable { get; set; } = true;
    }

    public static Snapshot? TakeSnapshot()
    {
        if (!TryOpen()) return null;
        try
        {
            var snapshot = new Snapshot();
            long total = 0;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                switch (format)
                {
                    case 2 or 9: // CF_BITMAP, CF_PALETTE: synthesized by Windows from CF_DIB
                        continue;
                    case 3 or 14 or 0x80 or 0x82 or 0x83 or 0x8E: // metafiles, owner-display
                    case >= 0x200 and <= 0x3FF: // private and GDI-object formats
                        snapshot.Restorable = false;
                        continue;
                }

                var handle = GetClipboardData(format);
                if (handle == 0) { snapshot.Restorable = false; continue; }
                var size = (long)GlobalSize(handle);
                if (size <= 0 || (total += size) > MaxSnapshotBytes) { snapshot.Restorable = false; continue; }

                var ptr = GlobalLock(handle);
                if (ptr == 0) { snapshot.Restorable = false; continue; }
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(ptr, data, 0, data.Length);
                    snapshot.Items.Add((format, data));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            return snapshot;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Puts Unicode text on the clipboard; returns the new sequence number or 0 on failure.</summary>
    public static uint SetText(string text, bool excludeFromHistory)
    {
        if (!TryOpen()) return 0;
        try
        {
            EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            if (!SetBytes(CF_UNICODETEXT, bytes)) return 0;
            if (excludeFromHistory && s_excludeFromHistory != 0) SetBytes(s_excludeFromHistory, [0, 0, 0, 0]);
        }
        finally
        {
            CloseClipboard();
        }
        return GetClipboardSequenceNumber();
    }

    public static bool Restore(Snapshot snapshot)
    {
        if (!TryOpen()) return false;
        try
        {
            EmptyClipboard();
            var ok = true;
            foreach (var (format, data) in snapshot.Items) ok &= SetBytes(format, data);
            return ok;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool SetBytes(uint format, byte[] data)
    {
        var mem = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (mem == 0) return false;
        var ptr = GlobalLock(mem);
        if (ptr == 0) { GlobalFree(mem); return false; }
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) == 0)
        {
            GlobalFree(mem); // ownership transfers only on success
            return false;
        }
        return true;
    }

    private static bool TryOpen()
    {
        // Another app may hold the clipboard briefly.
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(30);
        }
        return false;
    }
}
