using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NodePilot.Cli.Tests.Tls;

/// <summary>
/// In-memory certificates for the TLS tests. Nothing is written to a certificate store, so the
/// tests leave no machine state behind.
/// </summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateSelfSigned(string commonName, params string[] dnsNames)
        => CreateSelfSigned(
            commonName, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), dnsNames);

    public static X509Certificate2 CreateSelfSigned(
        string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames.Length == 0 ? new[] { commonName } : dnsNames) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));

        using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);

        // Schannel refuses to authenticate as a server with an ephemeral key, so the certificate is
        // round-tripped through a PFX before it is handed to SslStream.
        const string password = "nodepilot-test";
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable);
    }
}
