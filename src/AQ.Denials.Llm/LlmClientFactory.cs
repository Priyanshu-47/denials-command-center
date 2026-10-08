namespace AQ.Denials.Llm;

/// <summary>
/// The single place a client is chosen. Nothing else reads <c>LLM_*</c> from the environment.
/// </summary>
/// <remarks>
/// <para>
/// The three outcomes are deliberately distinct, because they mean three different things to the
/// person running this:
/// </para>
/// <list type="bullet">
/// <item><description><b>Nothing set</b> → <see cref="NullLlmClient"/>. The ordinary case: no AI is enabled, the
/// system runs in full, drafts are simply absent. Never a startup failure.</description></item>
/// <item><description><b>Set but unusable</b> (provider named without a model, unknown provider, hosted endpoint
/// without a key) → throws. This is a configuration mistake by someone who asked for AI, and
/// silently degrading would leave them believing it was working. Same reasoning as the seeded
/// credentials refusing to start outside Development: explicit intent plus a broken setting fails
/// loudly. Clear <c>LLM_PROVIDER</c> to run without AI.</description></item>
/// <item><description><b>Set and fine</b> → a real client. Outages at call time are caught by the drafting
/// layer, which degrades — that is the case the brief means by "must still work if the AI
/// service is unavailable".</description></item>
/// </list>
/// </remarks>
public static class LlmClientFactory
{
    /// <exception cref="LlmException">AI is configured but the configuration cannot work.</exception>
    public static ILlmClient Create()
    {
        var settings = LlmConfiguration.FromEnvironment();
        return settings is null
            ? new NullLlmClient()
            : new OpenAiCompatibleClient(settings);
    }
}
