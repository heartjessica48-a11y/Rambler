using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rambler.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON. Never stores secrets.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsService(string filePath)
    {
        FilePath = filePath;
        _current = Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rambler", "settings.json");

    public string FilePath { get; }

    /// <summary>Set when the file existed but could not be read (it is backed up and defaults are used).</summary>
    public string? LoadWarning { get; private set; }

    public event Action<AppSettings>? Changed;

    /// <summary>Returns a snapshot copy; callers can't mutate shared state.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    public void Save(AppSettings settings)
    {
        var copy = settings.Clone();
        copy.Normalize();
        if (copy.CleanupPrompt is not null &&
            string.Equals(copy.CleanupPrompt.Trim(), Cleanup.DefaultPrompts.CleanupSystemPrompt.Trim(), StringComparison.Ordinal))
        {
            copy.CleanupPrompt = null; // keep following the built-in default
        }

        var json = JsonSerializer.Serialize(copy, s_json);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);

        lock (_gate) _current = copy;
        Changed?.Invoke(copy.Clone());
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, s_json) ?? new AppSettings();
            Migrate(settings);
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadWarning = "Settings file was unreadable; defaults restored.";
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    /// <summary>
    /// Version 1 shipped Ctrl+Win+Space / Ctrl+Win+Shift+Space as defaults, which Windows itself uses.
    /// Clear them only if they are still those old defaults; shortcuts the user recorded are kept.
    /// </summary>
    internal static void Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion >= AppSettings.CurrentSchemaVersion) return;
        if (settings.HotkeyToggle == "Ctrl+Win+Space") settings.HotkeyToggle = string.Empty;
        if (settings.HotkeySmartBypass == "Ctrl+Win+Shift+Space") settings.HotkeySmartBypass = string.Empty;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    internal static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, s_json);
}
