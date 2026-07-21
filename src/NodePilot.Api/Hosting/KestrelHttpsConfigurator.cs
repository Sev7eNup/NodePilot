using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Binds Kestrel to HTTPS using a certificate pulled from the Windows certificate store by
/// thumbprint. Used by the production Windows-service deployment: the installer imports the
/// cert into <c>LocalMachine\My</c>, grants the gMSA read access to the private key, and
/// the thumbprint goes into <c>appsettings.Production.json</c>. Disabled by default, so dev
/// and test hosts keep their existing <c>Urls</c>-driven binding.
/// </summary>
public static class KestrelHttpsConfigurator
{
    public sealed class Options
    {
        public bool Enabled { get; init; }
        public int HttpsPort { get; init; } = 443;
        public int HttpPort { get; init; } = 80;
        public bool BindHttp { get; init; } = true;
        public string CertificateStore { get; init; } = "My";
        public string CertificateLocation { get; init; } = "LocalMachine";
        public string? CertificateThumbprint { get; init; }
        public bool RedirectHttpToHttps { get; init; } = true;
    }

    public static Options ReadOptions(IConfiguration config)
    {
        var section = config.GetSection("Kestrel:Https");
        return new Options
        {
            Enabled = section.GetValue<bool>("Enabled"),
            HttpsPort = section.GetValue<int?>("HttpsPort") ?? 443,
            HttpPort = section.GetValue<int?>("HttpPort") ?? 80,
            BindHttp = section.GetValue<bool?>("BindHttp") ?? true,
            CertificateStore = string.IsNullOrWhiteSpace(section["CertificateStore"])
                ? "My" : section["CertificateStore"]!,
            CertificateLocation = string.IsNullOrWhiteSpace(section["CertificateLocation"])
                ? "LocalMachine" : section["CertificateLocation"]!,
            CertificateThumbprint = section["CertificateThumbprint"],
            RedirectHttpToHttps = section.GetValue<bool?>("RedirectHttpToHttps") ?? true,
        };
    }

    /// <summary>
    /// Thumbprints copied from certmgr.msc contain spaces and often a hidden LRM/RLM control
    /// character at the start of the string. Normalise to hex-only, upper-case so a strict
    /// equality match against <see cref="X509Certificate2.Thumbprint"/> succeeds.
    /// </summary>
    public static string NormalizeThumbprint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        Span<char> buf = stackalloc char[raw.Length];
        var len = 0;
        foreach (var c in raw)
        {
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                buf[len++] = char.ToUpperInvariant(c);
        }
        return new string(buf[..len]);
    }

    public static StoreLocation ParseStoreLocation(string value) =>
        Enum.TryParse<StoreLocation>(value, ignoreCase: true, out var loc)
            ? loc
            : throw new InvalidOperationException(
                $"Kestrel:Https:CertificateLocation '{value}' is not a valid StoreLocation (CurrentUser | LocalMachine).");

    public static StoreName ParseStoreName(string value) =>
        Enum.TryParse<StoreName>(value, ignoreCase: true, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Kestrel:Https:CertificateStore '{value}' is not a valid StoreName (e.g. My, Root, CA).");

    internal static X509Certificate2 LoadCertificate(Options opts)
    {
        var thumbprint = NormalizeThumbprint(opts.CertificateThumbprint);
        if (thumbprint.Length == 0)
            throw new InvalidOperationException(
                "Kestrel:Https:Enabled=true but Kestrel:Https:CertificateThumbprint is missing or empty.");

        var location = ParseStoreLocation(opts.CertificateLocation);
        var storeName = ParseStoreName(opts.CertificateStore);

        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly);
        // We match strictly on normalized thumbprint — X509Certificate2Collection.Find with
        // validOnly=true would silently drop a cert the chain engine doesn't like (e.g. missing
        // intermediate on an offline server) and surface as "cert not found", which is the
        // wrong error.
        foreach (var cert in store.Certificates)
        {
            if (string.Equals(cert.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                if (!cert.HasPrivateKey)
                {
                    cert.Dispose();
                    throw new InvalidOperationException(
                        $"Certificate with thumbprint {thumbprint} was found in {location}\\{storeName} " +
                        "but has no accessible private key. The service account needs read access to the " +
                        "key material — see deploy/README.md for the MachineKeys ACL grant.");
                }
                return cert;
            }
        }
        throw new InvalidOperationException(
            $"Certificate with thumbprint {thumbprint} not found in {location}\\{storeName}. " +
            "Import the PFX into the correct store before starting the service.");
    }

    /// <summary>
    /// Wires Kestrel to listen on the configured HTTP/HTTPS ports using a cert from the
    /// Windows cert store, but only when <c>Kestrel:Https:Enabled</c> is true. No-op in dev.
    /// </summary>
    public static WebApplicationBuilder ConfigureKestrelFromWindowsCertStore(this WebApplicationBuilder builder)
    {
        var opts = ReadOptions(builder.Configuration);
        if (!opts.Enabled) return builder;

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "Kestrel:Https:Enabled=true requires Windows — cert-store lookup is unsupported on non-Windows hosts.");

        var cert = LoadCertificate(opts);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (opts.BindHttp)
            {
                kestrel.ListenAnyIP(opts.HttpPort);
            }
            kestrel.ListenAnyIP(opts.HttpsPort, listen =>
            {
                listen.UseHttps(cert);
            });
        });
        // Store for the app-stage redirection hook.
        builder.Services.AddSingleton(opts);
        return builder;
    }

    /// <summary>
    /// Enables HTTP→HTTPS redirection when Kestrel HTTPS was configured and
    /// <c>RedirectHttpToHttps=true</c>. Called from Program.cs after UseForwardedHeaders so
    /// the redirect targets the public-facing HTTPS port. No-op otherwise.
    /// </summary>
    public static WebApplication UseNodePilotHttpsRedirection(this WebApplication app)
    {
        var opts = app.Services.GetService<Options>();
        if (opts is null || !opts.Enabled || !opts.RedirectHttpToHttps) return app;

        app.UseHttpsRedirection();
        return app;
    }
}
