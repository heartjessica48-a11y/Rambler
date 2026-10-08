using System.Diagnostics;
using Rambler.Core.Gemini;

namespace Rambler.Core;

/// <summary>
/// Tiny diagnostic log for warnings and errors only. Every line is redacted, and callers must never
/// pass transcript text or audio-derived content. Disabled until <see cref="Initialize"/> is called.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object s_gate = new();
    private static string? s_path;

    public static string? FilePath => s_path;

    public static void Initialize(string path)
    {
        s_path = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { s_path = null; }
    }

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {Redactor.Redact(message)}";
        Debug.WriteLine(line);
        var path = s_path;
        if (path is null) return;

        lock (s_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
