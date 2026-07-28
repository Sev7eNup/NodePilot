using NodePilot.Ai;

namespace NodePilot.TestCommons;

/// <summary>
/// Test stub for <see cref="ILlmClientFactory"/>. The production services take the factory rather
/// than a pre-built <see cref="ILlmClient"/> (resolving the active profile can fail, and that has to
/// surface at call time, not at DI construction), so their unit tests need this wrapper around
/// <see cref="FakeLlmClient"/>.
///
/// <para>Records every <see cref="Create"/> call's overrides in <see cref="Connections"/>, and can
/// be told to fail like a missing active profile via <see cref="ThrowOnCreate"/>.</para>
/// </summary>
public sealed class FakeLlmClientFactory : ILlmClientFactory
{
    public FakeLlmClientFactory(FakeLlmClient client) => Client = client;

    /// <summary>Convenience for the common case: a factory wrapping a fresh, empty fake client.</summary>
    public FakeLlmClientFactory() : this(new FakeLlmClient()) { }

    public FakeLlmClient Client { get; }

    /// <summary>The overrides each <see cref="Create"/> call was given (null ⇒ "use the active profile").</summary>
    public List<LlmConnection?> Connections { get; } = new();

    /// <summary>When set, <see cref="Create"/> throws it — models "no active profile is configured".</summary>
    public LlmException? ThrowOnCreate { get; set; }

    public ILlmClient Create(LlmConnection? overrides = null)
    {
        Connections.Add(overrides);
        if (ThrowOnCreate is not null) throw ThrowOnCreate;
        return Client;
    }
}
