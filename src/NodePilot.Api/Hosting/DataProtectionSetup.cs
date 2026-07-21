using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace NodePilot.Api.Hosting;

/// <summary>Persistent, cluster-aware ASP.NET Core Data Protection key-ring setup.</summary>
public static class DataProtectionSetup
{
    private const string ApplicationName = "NodePilot";

    public static IServiceCollection AddNodePilotDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var configuredPath = configuration["DataProtection:KeyRingPath"];
        var keyRingPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data-protection-keys")
            : Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
        Directory.CreateDirectory(keyRingPath);

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

        var thumbprint = NormalizeThumbprint(configuration["DataProtection:CertificateThumbprint"]);
        if (!string.IsNullOrEmpty(thumbprint))
        {
            dataProtection.ProtectKeysWithCertificate(FindCertificate(thumbprint));
        }
        else if (OperatingSystem.IsWindows())
        {
            // Single-node safe default. HA/OIDC validation requires an explicit shared
            // certificate because LocalMachine DPAPI cannot be decrypted by another node.
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        return services;
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var certificate = matches.OfType<X509Certificate2>()
            .SingleOrDefault(c => c.HasPrivateKey);
        return certificate ?? throw new InvalidOperationException(
            $"DataProtection certificate '{thumbprint}' with private key was not found in LocalMachine\\My.");
    }

    private static string? NormalizeThumbprint(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
