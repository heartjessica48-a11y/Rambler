namespace Rambler.Core.Settings;

/// <summary>Secure storage for the Gemini API key (Windows Credential Manager in the app).</summary>
public interface ICredentialStore
{
    string? GetApiKey();
    void SetApiKey(string apiKey);
    void DeleteApiKey();
}

/// <summary>Volatile store for tests and environments without a credential vault.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private string? _key;
    public string? GetApiKey() => _key;
    public void SetApiKey(string apiKey) => _key = apiKey;
    public void DeleteApiKey() => _key = null;
}
