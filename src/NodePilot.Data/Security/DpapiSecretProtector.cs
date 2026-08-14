using System.Diagnostics;
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

    public byte[] Protect(string plaintext) => DataMetrics.MeasureCrypto("encrypt", ProviderName, () =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, _scope));

    public string Unprotect(byte[] blob) => DataMetrics.MeasureCrypto("decrypt", ProviderName, () =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, _scope)));
}
