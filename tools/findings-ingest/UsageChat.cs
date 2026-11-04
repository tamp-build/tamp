using System.Text;
using System.Text.Json;

namespace FindingsIngest;

// An IChatCompletion (the Tamp.Conformance BYOK seam) over the Anthropic Messages API that
// accumulates token usage — used by `generate` to report real rule-extraction cost.
sealed class UsageChat : Tamp.Conformance.IChatCompletion, IDisposable
{
    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly string _key, _model;
    private readonly int _maxTokens;
    public long InputTokens, OutputTokens;
    public int Calls;
    public UsageChat(string key, string model, int maxTokens = 8192) { _key = key; _model = model; _maxTokens = maxTokens; }
    public string ModelId => $"anthropic/{_model}";

    public string Complete(string system, string user)
    {
        var body = JsonSerializer.Serialize(new { model = _model, max_tokens = _maxTokens, system, messages = new[] { new { role = "user", content = user } } });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-api-key", _key);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic {(int)resp.StatusCode}: {payload}");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Calls++;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens", out var it)) InputTokens += it.GetInt64();
            if (u.TryGetProperty("output_tokens", out var ot)) OutputTokens += ot.GetInt64();
        }
        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content))
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());
        return sb.ToString();
    }
    public void Dispose() => _http.Dispose();
}
