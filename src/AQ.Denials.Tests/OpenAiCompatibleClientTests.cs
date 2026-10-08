using System.Net;
using System.Text;
using AQ.Denials.Llm;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>Throws on every request, standing in for a process that is not listening.</summary>
internal sealed class RefusingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException(
            "No connection could be made because the target machine actively refused it.");
}

/// <summary>
/// Reading <c>LLM_*</c> — the only place this system learns a provider or a key.
/// </summary>
/// <remarks>
/// Each test clears the variables on construction and restores them on disposal, so the suite
/// cannot inherit whatever the shell happened to have exported. These names are read by no other
/// test class, so the process-wide mutation cannot reach anything else.
/// </remarks>
public class LlmSettingsTests : IDisposable
{
    private static readonly string[] Keys =
        [
            "LLM_PROVIDER", "LLM_BASE_URL", "LLM_API_KEY", "LLM_MODEL", "LLM_TIMEOUT_SECONDS",
        ];

    private readonly Dictionary<string, string?> _saved = new();

    public LlmSettingsTests()
    {
        foreach (var key in Keys)
        {
            _saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);
    }

    [Fact]
    public void Nothing_set_is_the_ordinary_case_and_returns_no_settings_rather_than_throwing()
    {
        // The expected state must never be an error: no AI configured means drafts are absent,
        // not that the application refuses to run.
        Assert.Null(LlmConfiguration.FromEnvironment());
    }

    [Fact]
    public void A_blank_value_counts_as_unset()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "   ");
        Assert.Null(LlmConfiguration.FromEnvironment());
    }

    [Fact]
    public void A_provider_without_a_model_is_a_mistake_not_an_absence()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "ollama");

        var ex = Assert.Throws<LlmException>(() => LlmConfiguration.FromEnvironment());

        Assert.True(ex.NotConfigured);
        // It must tell the operator that they asked for AI and got it wrong, and how to opt out.
        Assert.Contains("configuration mistake, not an absent one", ex.Message, StringComparison.Ordinal);
        Assert.Contains("clear LLM_PROVIDER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_says_which_names_are_known()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "open-ai");
        Environment.SetEnvironmentVariable("LLM_MODEL", "m");

        var ex = Assert.Throws<LlmException>(() => LlmConfiguration.FromEnvironment());

        Assert.True(ex.NotConfigured);
        Assert.Contains("ollama", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LLM_BASE_URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hosted_endpoint_without_a_key_is_reported_before_the_401_would_be()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "openai");
        Environment.SetEnvironmentVariable("LLM_MODEL", "gpt-4o-mini");

        var ex = Assert.Throws<LlmException>(() => LlmConfiguration.FromEnvironment());

        Assert.Contains("LLM_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_endpoint_needs_no_key()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "ollama");
        Environment.SetEnvironmentVariable("LLM_MODEL", "qwen2.5-coder:3b");

        var settings = LlmConfiguration.FromEnvironment();

        Assert.NotNull(settings);
        Assert.Equal("http://localhost:11434/v1", settings!.BaseUrl);
        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public void An_explicit_base_url_overrides_the_known_default()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "ollama");
        Environment.SetEnvironmentVariable("LLM_MODEL", "m");
        Environment.SetEnvironmentVariable("LLM_BASE_URL", "http://gpu-box:11434/v1");
        // Not loopback, so the "hosted endpoint needs a key" rule applies — which is itself worth
        // seeing hold, since a LAN box is a different case from localhost.
        Environment.SetEnvironmentVariable("LLM_API_KEY", "lan-box-key");

        var settings = LlmConfiguration.FromEnvironment();

        Assert.Equal("http://gpu-box:11434/v1", settings!.BaseUrl);
    }

    [Fact]
    public void The_key_is_rendered_as_present_or_absent_and_never_as_itself()
    {
        Environment.SetEnvironmentVariable("LLM_PROVIDER", "ollama");
        Environment.SetEnvironmentVariable("LLM_MODEL", "m");
        Environment.SetEnvironmentVariable("LLM_API_KEY", "super-secret-value-123456");

        var settings = LlmConfiguration.FromEnvironment();

        Assert.NotNull(settings);
        // A configuration record that prints its own value is how keys end up in log files.
        Assert.DoesNotContain("super-secret-value-123456", settings!.ToString(), StringComparison.Ordinal);
        Assert.Contains("apiKey=set", settings.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The one HTTP client that reaches a model: wire format, error classification, and the key.
/// </summary>
public class OpenAiCompatibleClientTests
{
    private const string Secret = "sk-test-key-that-must-not-leak";

    private static OpenAiCompatibleClient Client(StubHandler handler, string? key = Secret) =>
        new(
            new LlmSettings(
                Provider: "ollama",
                BaseUrl: "http://localhost:11434/v1",
                Model: "test-model",
                Timeout: TimeSpan.FromSeconds(5),
                ApiKey: key),
            handler);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_successful_call_sends_the_openai_shape_and_reads_the_reply()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""
                {
                  "model": "test-model",
                  "choices": [ { "message": { "role": "assistant", "content": "hello" } } ],
                  "usage": { "prompt_tokens": 10, "completion_tokens": 3 }
                }
                """),
        };

        var response = await Client(handler).CompleteAsync(
            new LlmRequest(
                SystemPrompt: "You draft appeals.",
                Messages: [new LlmMessage("user", "draft this")]));

        Assert.Equal("hello", response.Text);
        Assert.Equal(10, response.InputTokens);
        Assert.Equal(3, response.OutputTokens);

        Assert.Contains("\"model\":\"test-model\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"system\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"temperature\":0", handler.LastBody, StringComparison.Ordinal);

        // It authenticates — the point of a key is that it is sent, just nowhere else.
        Assert.Equal(Secret, handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task With_no_key_no_authorization_header_is_sent()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{ "choices": [ { "message": { "content": "ok" } } ] }"""),
        };

        await Client(handler, key: null).CompleteAsync(
            new LlmRequest("system", [new LlmMessage("user", "hi")]));

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task A_refused_endpoint_is_an_outage_the_system_continues_without()
    {
        var client = new OpenAiCompatibleClient(
            new LlmSettings("ollama", "http://localhost:11434/v1", "m", TimeSpan.FromSeconds(5), null),
            new RefusingHandler());

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        // The distinction that decides degradation: unreachable is retryable, not broken.
        Assert.True(ex.Transient);
        Assert.False(ex.NotConfigured);
        Assert.Contains("unreachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continues without drafts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_credentials_are_classified_as_configuration_not_as_an_outage()
    {
        var handler = new StubHandler
        {
            Responder = _ => Status(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid api key"}}"""),
        };

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(handler).CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        Assert.True(ex.NotConfigured);
        Assert.Contains("LLM_API_KEY", ex.Message, StringComparison.Ordinal);
        // Reported as a problem with the key — without quoting the key it is a problem with.
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_faults_and_rate_limits_are_transient()
    {
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
        {
            var handler = new StubHandler { Responder = _ => Status(status, "try later") };

            var ex = await Assert.ThrowsAsync<LlmException>(() =>
                Client(handler).CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

            Assert.True(ex.Transient, $"{status} should be retryable");
            Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_request_the_provider_won_t_accept_says_what_format_this_client_speaks()
    {
        var handler = new StubHandler
        {
            // 400, not 501: a provider that cannot read the request shape rejects it as a client
            // error. (501 is also a 5xx, so it is correctly treated as retryable.)
            Responder = _ => Status(HttpStatusCode.BadRequest, "unsupported request"),
        };

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(handler).CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        // Honest about a limitation rather than silently retrying something that will never work.
        Assert.False(ex.Transient);
        Assert.Contains("Anthropic", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LLM_BASE_URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_permanent_because_retrying_cannot_make_it_parse()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>gateway error</html>", Encoding.UTF8, "text/html"),
            },
        };

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(handler).CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        Assert.False(ex.Transient);
        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_with_no_message_content_is_refused_rather_than_returned_empty()
    {
        var handler = new StubHandler
        {
            // A well-formed envelope with no content inside it — the shape a model produces when
            // it declines. An empty draft must never be read as an accepted one.
            Responder = _ => Json("""{ "model": "m", "choices": [ { "message": { "content": "  " } } ] }"""),
        };

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(handler).CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        Assert.Contains("no message content", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_model_is_refused_before_any_request_is_made()
    {
        var handler = new StubHandler();
        var client = new OpenAiCompatibleClient(
            new LlmSettings("ollama", "http://localhost:11434/v1", "", TimeSpan.FromSeconds(5), null),
            handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.CompleteAsync(new LlmRequest("system", [new LlmMessage("user", "hi")])));

        Assert.True(ex.NotConfigured);
        // Nothing was sent — a blank model name must not become a request that fails at the door.
        Assert.Null(handler.LastRequest);
    }
}
