using System.Net;
using System.Text;

namespace Rambler.Core.Tests.Fakes;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body);

/// <summary>Deterministic HTTP responses for Gemini REST tests; records every request.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<RecordedRequest, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(r);
            return r;
        });
        return this;
    }

    public FakeHttpHandler Respond(Func<RecordedRequest, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(recorded);
        if (_responses.Count == 0) throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        return _responses.Dequeue()(recorded);
    }
}
