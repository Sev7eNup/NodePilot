using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// Default <see cref="ISecretProtector"/> backed by Windows DPAPI. Behaviour is identical
/// to the pre-abstraction <c>CredentialStore</c>: <see cref="DataProtectionScope"/> drives
/// the user-vs-machine binding semantics. <c>CurrentUser</c> is the dev default,
/// <c>LocalMachine</c> is the operator-deployment recommendation.
/// <para>
/// Note: DPAPI-encrypted blobs are <b>machine-bound</b> regardless of scope. Any cluster
/// deployment that needs cross-host portability must switch the registered protector to
/// <c>AesGcmSecretProtector</c> via <c>Secrets:Provider=AesGcm</c>.
/// </para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly DataProtectionScope _scope;

    public string ProviderName => "Dpapi";

    public DpapiSecretProtector(DataProtectionScope scope)
    {
        _scope = scope;
    }

    public byte[] Protect(string plaintext)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var result = ProtectedData.Protect(bytes, null, _scope);
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "encrypt"),
                new("provider", ProviderName),
                new("result", "success"));
            var tags = new TagList { new("operation", "encrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            return result;
        }
        catch
        {
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "encrypt"),
                new("provider", ProviderName),
                new("result", "failure"));
            var tags = new TagList { new("operation", "encrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            throw;
        }
    }

    public string Unprotect(byte[] blob)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var bytes = ProtectedData.Unprotect(blob, null, _scope);
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "decrypt"),
                new("provider", ProviderName),
                new("result", "success"));
            var tags = new TagList { new("operation", "decrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            sw.Stop();
            DataMetrics.CredentialCryptoCalls.Add(1,
                new("operation", "decrypt"),
                new("provider", ProviderName),
                new("result", "failure"));
            var tags = new TagList { new("operation", "decrypt"), new("provider", ProviderName) };
            DataMetrics.CredentialCryptoDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            throw;
        }
    }
}
