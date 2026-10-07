namespace AQ.Denials.Llm;

/// <summary>One turn of conversation with a language model, as this system needs to see it.</summary>
public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    double Temperature = 0.0,
    int? MaxTokens = null);

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmResponse(
    string Text,
    string? Model,
    int InputTokens,
    int OutputTokens,
    string Provider);

/// <summary>Why a call could not be completed.</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>True when retrying unchanged might succeed (rate limit, timeout, 5xx).</summary>
    public bool Transient { get; init; }

    /// <summary>True when the call was refused because no key or provider is configured.</summary>
    public bool NotConfigured { get; init; }
}

/// <summary>
/// The only door this system has to a language model. Nothing outside this interface may read an
/// API key, name a provider, or build an HTTP client to a model endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Two properties are enforced here rather than left to convention, because both are the sort of
/// thing that leaks quietly:
/// </para>
/// <list type="bullet">
/// <item><description><b>No key or provider appears in any signature.</b> Configuration is read from environment
/// variables by the implementation (see <c>README.md</c>'s variable table); the caller only ever
/// describes the work it wants done. A key cannot be logged or asserted on by code that cannot
/// see it.</description></item>
/// <item><description><b>Every implementation must be substitutable.</b> <see cref="NullLlmClient"/> is the default
/// in tests and in environments without a key: it refuses clearly instead of returning a
/// plausible-looking invented answer, so an unconfigured system degrades to "no draft" rather
/// than to fabrication.</description></item>
/// </list>
/// <para>
/// The interface deliberately returns no parsed or structured output: interpretation of the
/// response belongs to the caller that knows what it asked for, and belongs to a test.
/// </para>
/// </remarks>
public interface ILlmClient
{
    /// <summary>Which provider is configured, or <c>null</c> when none is.</summary>
    string? ConfiguredProvider { get; }

    /// <summary>False when no provider/key is available; callers must not attempt a call.</summary>
    bool IsConfigured { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default client: present whenever no provider is configured, and the only client used by
/// tests that do not specifically exercise a live model.
/// </summary>
/// <remarks>
/// It throws rather than returning a stub answer. Returning an empty string or a canned sentence
/// would let a caller believe the model had said something, which is precisely the failure mode a
/// grounding check exists to prevent.
/// </remarks>
public sealed class NullLlmClient : ILlmClient
{
    public string? ConfiguredProvider => null;
    public bool IsConfigured => false;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new LlmException(
            "No LLM provider is configured. Set LLM_PROVIDER and the matching API key; "
          + "see README.md, environment variable table. Refusing to answer rather than "
          + "produce an ungrounded draft.")
        { NotConfigured = true };
}
