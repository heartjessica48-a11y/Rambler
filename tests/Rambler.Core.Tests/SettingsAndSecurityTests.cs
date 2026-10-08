using System.Net;
using System.Net.WebSockets;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambler-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var s = new SettingsService(FilePath).Current;
        Assert.Equal(DictationMode.PromptCleanup, s.DefaultMode);
        Assert.Equal("", s.HotkeyToggle); // no shortcuts out of the box: the user records their own
        Assert.Equal("", s.HotkeySmartBypass);
        Assert.Equal(35, s.TechnicalRefinement);
        Assert.Equal("gemini-3.5-transcribe-live", s.LiveModel);
        Assert.Equal("gemini-3.5-transcribe", s.RecordedModel);
        Assert.False(s.StartWithWindows);
        Assert.Null(s.CleanupPrompt);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var service = new SettingsService(FilePath);
        var s = service.Current;
        s.DefaultMode = DictationMode.SmartOnly;
        s.HotkeyToggle = "Ctrl+Alt+D";
        s.Theme = ThemePreference.Dark;
        s.CustomVocabulary = ["Kubernetes", "Rambler"];
        s.CleanupPrompt = "My own prompt";
        s.TechnicalRefinement = 70;
        s.MicrophoneDeviceId = "{0.0.1.00000000}.{abc}";
        service.Save(s);

        var reloaded = new SettingsService(FilePath).Current;
        Assert.Equal(DictationMode.SmartOnly, reloaded.DefaultMode);
        Assert.Equal("Ctrl+Alt+D", reloaded.HotkeyToggle);
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.Equal(["Kubernetes", "Rambler"], reloaded.CustomVocabulary);
        Assert.Equal("My own prompt", reloaded.CleanupPrompt);
        Assert.Equal(70, reloaded.TechnicalRefinement);
        Assert.Equal("{0.0.1.00000000}.{abc}", reloaded.MicrophoneDeviceId);
    }

    [Fact]
    public void Empty_shortcut_means_disabled_and_survives_reload()
    {
        var service = new SettingsService(FilePath);
        var s = service.Current;
        s.HotkeyToggle = "Ctrl+Alt+D";
        s.HotkeySmartBypass = "";
        service.Save(s);
        var reloaded = new SettingsService(FilePath).Current;
        Assert.Equal("", reloaded.HotkeySmartBypass);
        Assert.Equal("Ctrl+Alt+D", reloaded.HotkeyToggle);
    }

    [Fact]
    public void Old_default_shortcuts_are_cleared_once_but_recorded_ones_are_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"schemaVersion":1,"hotkeyToggle":"Ctrl+Win+Space","hotkeySmartBypass":"Ctrl+Alt+S"}""");
        var s = new SettingsService(FilePath).Current;
        Assert.Equal("", s.HotkeyToggle);
        Assert.Equal("Ctrl+Alt+S", s.HotkeySmartBypass);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void Migration_runs_only_once()
    {
        // A user on schema 2 who deliberately records Ctrl+Win+Space keeps it.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"schemaVersion":2,"hotkeyToggle":"Ctrl+Win+Space"}""");
        Assert.Equal("Ctrl+Win+Space", new SettingsService(FilePath).Current.HotkeyToggle);
    }

    [Fact]
    public void Prompt_equal_to_default_is_stored_as_default()
    {
        var service = new SettingsService(FilePath);
        var s = service.Current;
        s.CleanupPrompt = DefaultPrompts.CleanupSystemPrompt;
        service.Save(s);
        Assert.Null(new SettingsService(FilePath).Current.CleanupPrompt);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_are_used()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var service = new SettingsService(FilePath);
        Assert.NotNull(service.LoadWarning);
        Assert.Equal(DictationMode.PromptCleanup, service.Current.DefaultMode);
        Assert.True(File.Exists(FilePath + ".bak"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"technicalRefinement": 400, "maxRecordingMinutes": 0, "liveModel": "  "}""");
        var s = new SettingsService(FilePath).Current;
        Assert.Equal(100, s.TechnicalRefinement);
        Assert.Equal(1, s.MaxRecordingMinutes);
        Assert.Equal(AppSettings.DefaultLiveModel, s.LiveModel);
    }

    [Fact]
    public void Current_returns_isolated_copies()
    {
        var service = new SettingsService(FilePath);
        var a = service.Current;
        a.CustomVocabulary.Add("mutated");
        Assert.Empty(service.Current.CustomVocabulary);
    }

    [Fact]
    public void Settings_never_contain_secrets()
    {
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(),
            p => p.Name.Contains("key", StringComparison.OrdinalIgnoreCase) && !p.Name.Contains("Hotkey", StringComparison.Ordinal));

        var service = new SettingsService(FilePath);
        var credentials = new InMemoryCredentialStore();
        credentials.SetApiKey("AIzaSECRETSECRETSECRETSECRETSECRET12345");
        service.Save(service.Current);
        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("AIza", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public class RedactorAndErrorMappingTests
{
    [Fact]
    public void Redacts_query_keys_headers_google_key_patterns_and_registered_secrets()
    {
        const string key = "AIzaSyA1234567890abcdefghijklmnopqrstuv";
        Redactor.RegisterSecret("custom-secret-value");
        var text = $"GET https://x.googleapis.com/v1?key={key}&alt=json x-goog-api-key: {key} and custom-secret-value";
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain(key, redacted);
        Assert.DoesNotContain("custom-secret-value", redacted);
        Assert.Contains("[REDACTED]", redacted);
        Redactor.RegisterSecret(null);
    }

    [Fact]
    public void Gemini_exception_messages_are_redacted()
    {
        var ex = new GeminiException(GeminiErrorKind.Unknown, "failed for key=AIzaSyA1234567890abcdefghijklmnopqrstuv");
        Assert.DoesNotContain("AIzaSy", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "", GeminiErrorKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.Forbidden, "", GeminiErrorKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.TooManyRequests, "", GeminiErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.NotFound, "", GeminiErrorKind.ModelUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid JSON payload received. Unknown name \"foo\""}}""", GeminiErrorKind.BadRequest)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid."}}""", GeminiErrorKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", GeminiErrorKind.Server)]
    public void Maps_http_errors(HttpStatusCode status, string body, GeminiErrorKind expected) =>
        Assert.Equal(expected, GeminiHttp.MapHttpError(status, body).Kind);

    [Theory]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "API key not valid. Please pass a valid API key.", GeminiErrorKind.InvalidApiKey)]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "models/x is not found for API version v1beta, or is not supported for bidiGenerateContent.", GeminiErrorKind.ModelUnavailable)]
    [InlineData(WebSocketCloseStatus.InternalServerError, "You exceeded your current quota", GeminiErrorKind.QuotaExceeded)]
    [InlineData(WebSocketCloseStatus.InvalidPayloadData, "Request contains an invalid argument.", GeminiErrorKind.BadRequest)]
    [InlineData(WebSocketCloseStatus.InternalServerError, "Internal error encountered.", GeminiErrorKind.Server)]
    [InlineData(null, null, GeminiErrorKind.Network)]
    public void Maps_live_close_frames(WebSocketCloseStatus? status, string? description, GeminiErrorKind expected) =>
        Assert.Equal(expected, GeminiLiveConnection.MapClose(status, description).Kind);

    [Fact]
    public void Only_key_and_quota_errors_are_fatal()
    {
        Assert.True(new GeminiException(GeminiErrorKind.InvalidApiKey, "x").IsFatal);
        Assert.True(new GeminiException(GeminiErrorKind.QuotaExceeded, "x").IsFatal);
        Assert.False(new GeminiException(GeminiErrorKind.Network, "x").IsFatal);
        Assert.False(new GeminiException(GeminiErrorKind.ModelUnavailable, "x").IsFatal);
    }

    [Fact]
    public void Endpoints_never_embed_keys_and_normalize_model_names()
    {
        Assert.Equal("models/gemini-3.6-flash", GeminiEndpoints.ModelResource("gemini-3.6-flash"));
        Assert.Equal("models/gemini-3.6-flash", GeminiEndpoints.ModelResource("models/gemini-3.6-flash"));
        Assert.Equal("wss", GeminiEndpoints.LiveWebSocket.Scheme);
        Assert.Equal("https", GeminiEndpoints.GenerateContent("m").Scheme);
        Assert.Empty(GeminiEndpoints.LiveWebSocket.Query);
    }
}
