using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AQ.Denials.Llm;

/// <summary>
/// Everything a call needs, resolved once from the environment.
/// </summary>
/// <remarks>
/// The key is an ordinary property here but is consumed immediately into a private field by the
/// client and never rendered — not by <c>ToString</c>, not by an exception message, not by a
/// diagnostic dump, not by a test assertion. Configuration records that get logged are how API
/// keys end up in log files.
/// </remarks>
public sealed record LlmSettings(
    string Provider,
    string BaseUrl,
    string Model,
    TimeSpan Timeout,
    string? ApiKey)
{
    public override string ToString() =>
        $"LlmSettings(provider={Provider}, model={Model}, baseUrl={BaseUrl}, "
      + $"timeout={Timeout.TotalSeconds:0}s, apiKey={(ApiKey is null ? "none" : "set")})";
}

/// <summary>How the environment is read, and what "not configured" is allowed to mean.</summary>
public static class LlmConfiguration
{
    // Providers whose wire format this build actually speaks. See D29 for why the list is short
    // and stated rather than long and implied.
    private static readonly IReadOnlyDictionary<string, string> KnownBaseUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Ollama serves the OpenAI schema on /v1 as well as its own on /api — using the
            // former means one client covers a laptop model and a hosted one alike.
            ["ollama"] = "http://localhost:11434/v1",
            ["openai"] = "https://api.openai.com/v1",
            ["gemini"] = "https://generativelanguage.googleapis.com/v1beta/openai",
            ["openrouter"] = "https://openrouter.ai/api/v1",
            ["together"] = "https://api.together.xyz/v1",
            ["groq"] = "https://api.groq.com/openai/v1",
        };

    public static IReadOnlyCollection<string> KnownProviders => KnownBaseUrls.Keys.ToArray();

    /// <summary>
    /// Read the environment once.
    /// </summary>
    /// <returns>
    /// <c>null</c> when nothing is configured — the ordinary, expected state, which must leave the
    /// system running without drafts rather than refusing to start. A thrown
    /// <see cref="LlmException"/> means something <b>is</b> configured but unusable, which is a
    /// different condition and must not be mistaken for absence.
    /// </returns>
    public static LlmSettings? FromEnvironment()
    {
        var provider = Read("LLM_PROVIDER");
        if (provider is null) return null;

        var model = Read("LLM_MODEL");
        if (model is null)
            throw new LlmException(
                "LLM_PROVIDER is set but LLM_MODEL is empty, so the provider would be called "
              + "with no model. That is a configuration mistake, not an absent one — set "
              + "LLM_MODEL, or clear LLM_PROVIDER to run without AI.")
            { NotConfigured = true };

        var explicitBaseUrl = Read("LLM_BASE_URL");
        var baseUrl = explicitBaseUrl;
        if (baseUrl is null)
        {
            if (!KnownBaseUrls.TryGetValue(provider, out var known))
                throw new LlmException(
                    $"LLM_PROVIDER='{provider}' is not one of the providers with a known endpoint "
                  + $"({string.Join(", ", KnownBaseUrls.Keys)}). Set LLM_BASE_URL to point at any "
                  + "other OpenAI-compatible gateway.")
                { NotConfigured = true };
            baseUrl = known;
        }

        var key = Read("LLM_API_KEY");
        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Read("LLM_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
                ? seconds
                : 60);

        // A hosted endpoint without a key fails at the door with 401, which is a worse error than
        // saying plainly that the key was never supplied.
        if (key is null && !IsLocal(baseUrl))
            throw new LlmException(
                $"LLM_PROVIDER='{provider}' points at '{baseUrl}', which needs LLM_API_KEY. "
              + "Set it, or point LLM_BASE_URL at a local endpoint that does not require one.")
            { NotConfigured = true };

        return new LlmSettings(provider, baseUrl.TrimEnd('/'), model, timeout, key);
    }

    /// <summary>Environment value, or <c>null</c> for absent and for blank.</summary>
    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool IsLocal(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The only HTTP client this system owns that talks to a language model.
/// </summary>
/// <remarks>
/// <para>
/// It speaks the OpenAI <c>/chat/completions</c> schema, which is the one format Ollama, OpenAI
/// and Gemini's gateway all expose — so a single implementation covers a laptop model and a hosted
/// one, and switching is a line in <c>.env</c>. Measured against local Ollama, this endpoint and
/// Ollama's native one perform the same (~1 s warm); the difference a cold start makes is model
/// load, not wire format.
/// </para>
/// <para>
/// <b>What it deliberately does not claim:</b> Anthropic's Messages API uses a different schema
/// and is <b>not implemented</b>, because an untested client is worse than an absent one — it
/// would advertise support nobody has verified. Point <c>LLM_BASE_URL</c> at an OpenAI-compatible
/// gateway to reach it; the error raised says exactly that.
/// </para>
/// <para>
/// Errors are classified rather than aggregated. <see cref="LlmException.NotConfigured"/> means
/// this system never had what it needed; <see cref="LlmException.Transient"/> means retrying may
/// work. The drafting layer degrades on both but reports them differently — "AI is not enabled"
/// and "AI is enabled but unreachable" are different things to tell a user.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleClient : ILlmClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly LlmSettings _settings;

    public OpenAiCompatibleClient(LlmSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ownsHttp = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = settings.Timeout;
    }

    public string? ConfiguredProvider => _settings.Provider;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Model) && !string.IsNullOrWhiteSpace(_settings.BaseUrl);

    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
            throw new LlmException(
                $"Provider '{_settings.Provider}' is not usable: model or endpoint is empty.")
            { NotConfigured = true };

        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt },
        };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));

        var payload = new
        {
            model = _settings.Model,
            messages,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{_settings.BaseUrl}/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        if (_settings.ApiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(
                $"The model at {_settings.BaseUrl} did not respond within "
              + $"{_settings.Timeout.TotalSeconds:0}s.")
            { Transient = true };
        }
        catch (HttpRequestException ex)
        {
            // The most important classification in this class: "the AI service is unavailable"
            // must reach the caller as a retryable outage, not as a broken system.
            throw new LlmException(
                $"The model endpoint at {_settings.BaseUrl} is unreachable ({ex.Message}). "
              + "The system continues without drafts.",
                ex)
            { Transient = true };
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Classify(response.StatusCode, body);

            return Parse(body);
        }
    }

    private LlmException Classify(HttpStatusCode status, string body)
    {
        var detail = Truncate(body, 300);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new LlmException(
                $"The model provider rejected the credentials (HTTP {(int)status}). Check "
              + $"LLM_API_KEY for provider '{_settings.Provider}'. Provider said: {detail}")
            { NotConfigured = true };

        if (status == HttpStatusCode.TooManyRequests)
            return new LlmException($"Rate limited by the model provider: {detail}")
            { Transient = true };

        if ((int)status >= 500)
            return new LlmException($"Model provider returned HTTP {(int)status}: {detail}")
            { Transient = true };

        return new LlmException(
            $"Model provider returned HTTP {(int)status}. This build speaks the OpenAI "
          + "/chat/completions schema; if this is Anthropic's Messages API, point LLM_BASE_URL at "
          + $"an OpenAI-compatible gateway. Provider said: {detail}");
    }

    private static LlmResponse Parse(string body)
    {
        // An unreadable response is a permanent fault, not a transient one: retrying a body this
        // client cannot parse will not make it parseable.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LlmException(
                $"The model provider returned something that is not JSON: {Truncate(body, 200)}",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;

            var content = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var text)
                    ? text.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(content))
                throw new LlmException(
                    "The model returned no message content. Provider said: " + Truncate(body, 300));

            return new LlmResponse(
                Text: content!,
                Model: root.TryGetProperty("model", out var model) ? model.GetString() : null,
                InputTokens: Number(root, "prompt_tokens"),
                OutputTokens: Number(root, "completion_tokens"),
                Provider: "openai-compatible");
        }
    }

    private static int Number(JsonElement root, string field) =>
        root.TryGetProperty("usage", out var usage)
        && usage.TryGetProperty(field, out var v)
        && v.TryGetInt32(out var n)
            ? n
            : 0;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
