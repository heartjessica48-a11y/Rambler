namespace Rambler.Core.Gemini;

public enum GeminiErrorKind
{
    Unknown,
    InvalidApiKey,
    QuotaExceeded,
    ModelUnavailable,
    BadRequest,
    Network,
    Timeout,
    Server,
    Blocked,
    EmptyResult,
}

/// <summary>A Gemini failure with a short, user-facing message. Messages are always redacted.</summary>
public sealed class GeminiException : Exception
{
    public GeminiException(GeminiErrorKind kind, string message, Exception? inner = null)
        : base(Redactor.Redact(message), inner)
    {
        Kind = kind;
    }

    public GeminiErrorKind Kind { get; }

    /// <summary>Errors where retrying or falling back with the same key cannot help.</summary>
    public bool IsFatal => Kind is GeminiErrorKind.InvalidApiKey or GeminiErrorKind.QuotaExceeded;

    public string UserMessage => Kind switch
    {
        GeminiErrorKind.InvalidApiKey => "Gemini rejected the API key. Check it in Settings › Gemini.",
        GeminiErrorKind.QuotaExceeded => "Gemini quota or rate limit reached. Wait a moment, or check your plan.",
        GeminiErrorKind.ModelUnavailable => $"Model unavailable: {Message}",
        GeminiErrorKind.Network => "Network error talking to Gemini. Check your connection.",
        GeminiErrorKind.Timeout => "Gemini took too long to respond.",
        GeminiErrorKind.Blocked => "Gemini blocked the response.",
        GeminiErrorKind.EmptyResult => "Gemini returned no text.",
        _ => Message,
    };
}
