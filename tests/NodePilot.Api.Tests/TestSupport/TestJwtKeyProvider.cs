using NodePilot.Api.Security;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Fixed-key <see cref="IJwtKeyProvider"/> for auth tests. The production path validates
/// <c>Jwt:Key</c> once at startup (security-audit finding M-2) and injects the provider; tests
/// mirror that contract with the same 32+ character key their in-memory configs emit, so tokens
/// minted by one component validate in another. Lives here because seven auth test files
/// previously carried identical private copies. The interface is Api-side, so the type cannot
/// move further down into TestCommons.
/// </summary>
public sealed class TestJwtKeyProvider : IJwtKeyProvider
{
    /// <summary>Same literal the auth test configs use for <c>Jwt:Key</c>.</summary>
    public const string DefaultKey = "NodePilot-Test-Secret-Key-Minimum-32-Characters!";

    public string Key => DefaultKey;
}
