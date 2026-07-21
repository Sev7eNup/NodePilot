using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// Decorator that lets a deployment migrate secret ciphertexts from one
/// <see cref="ISecretProtector"/> to another without re-entering every value by hand.
/// <list type="bullet">
///   <item><description><see cref="Protect"/> always uses the new (active) protector —
///   so any value written under the migration regime ends up in the new format
///   immediately.</description></item>
///   <item><description><see cref="Unprotect"/> tries the active protector first; on
///   failure (header mismatch, MAC mismatch, format error) falls back to the legacy
///   protector. Successful legacy reads are counted via the
///   <c>nodepilot_credential_crypto_legacy_reads_total</c> meter so an operator can
///   tell when the sweep has fully migrated all rows and the legacy config can be
///   removed.</description></item>
/// </list>
/// <para>
/// Wire only when <c>Secrets:LegacyProvider</c> is set in addition to
/// <c>Secrets:Provider</c>. The intended operational lifecycle is: enable legacy
/// alongside the new provider → run <c>POST /api/secrets/reencrypt</c> → confirm
/// counter at zero new legacy reads → drop the legacy config from settings.
/// </para>
/// </summary>
public sealed class MigratingSecretProtector : ISecretProtector
{
    private readonly ISecretProtector _active;
    private readonly ISecretProtector _legacy;
    private readonly ILogger<MigratingSecretProtector>? _logger;

    public string ProviderName => $"{_active.ProviderName}+{_legacy.ProviderName}-fallback";

    public MigratingSecretProtector(ISecretProtector active, ISecretProtector legacy,
        ILogger<MigratingSecretProtector>? logger = null)
    {
        _active = active;
        _legacy = legacy;
        _logger = logger;
    }

    public byte[] Protect(string plaintext) => _active.Protect(plaintext);

    public string Unprotect(byte[] blob)
    {
        try
        {
            return _active.Unprotect(blob);
        }
        catch (Exception activeEx) when (
            activeEx is CryptographicException || activeEx is FormatException || activeEx is ArgumentException)
        {
            // Active protector couldn't read this blob — try the legacy one. If the
            // legacy ALSO fails, throw a combined error pointing at both attempts so
            // the operator sees the full diagnostic, not just the legacy failure.
            try
            {
                var plain = _legacy.Unprotect(blob);
                DataMetrics.CredentialCryptoLegacyReads.Add(1,
                    new KeyValuePair<string, object?>("legacy_provider", _legacy.ProviderName));
                _logger?.LogDebug(
                    "Decrypted blob via legacy protector ({Legacy}); active ({Active}) rejected with {Error}. " +
                    "Re-encrypt sweep will move this row to the active format.",
                    _legacy.ProviderName, _active.ProviderName, activeEx.GetType().Name);
                return plain;
            }
            catch (Exception legacyEx) when (
                legacyEx is CryptographicException || legacyEx is FormatException || legacyEx is ArgumentException)
            {
                throw new CryptographicException(
                    $"Decrypt failed under both protectors. Active ({_active.ProviderName}): " +
                    $"{activeEx.GetType().Name}: {activeEx.Message}. Legacy ({_legacy.ProviderName}): " +
                    $"{legacyEx.GetType().Name}: {legacyEx.Message}.",
                    legacyEx);
            }
        }
    }
}
