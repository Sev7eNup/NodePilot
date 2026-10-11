using System.Net;
using System.Runtime.Versioning;
using NodePilot.Cli.Settings;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Auth;

[SupportedOSPlatform("windows")]
public sealed class SessionResolver
{
    private readonly ConfigStore _config;
    private readonly TokenStore _tokens;

    public SessionResolver(ConfigStore config, TokenStore tokens)
    {
        _config = config;
        _tokens = tokens;
    }

    public SessionContext Resolve(GlobalSettings settings)
    {
        var cfg = _config.Load();
        var profile = _config.ResolveProfileName(settings.Profile, cfg);
        var server = _config.ResolveServer(settings.Server, profile, cfg);
        var session = _tokens.Load(profile);

        // A DPAPI session is authority-bound. Environment/profile overrides must never
        // redirect its bearer token to another origin; paths may differ, but
        // scheme + normalized host + effective port must match exactly.
        if (session is not null
            && !ClientSessionSecurity.HasSameServerOrigin(session.Server, server))
        {
            session = null;
        }

        // A stored pin is authority-bound for the same reason a token is: it overrides hostname
        // validation, so it may only be presented to the origin it was accepted for.
        var pin = _config.ResolveTlsThumbprint(settings.TlsThumbprint, profile, server, cfg);

        var switcherKey = ReadSwitcherKey(
            Environment.GetEnvironmentVariable(SwitcherKeyFileVariable), server);

        return new SessionContext
        {
            Profile = profile,
            Server = server,
            Session = switcherKey is null ? session : null,
            SwitcherKey = switcherKey,
            AllowInsecureLoopback = settings.AllowInsecureLoopback,
            Tls = new ClientTlsOptions(pin.Value, ConfigStore.ResolveSkipTlsVerification(settings.InsecureTls)),
            TlsPinNotice = pin.IgnoredReason,
        };
    }

    public const string SwitcherKeyFileVariable = "NODEPILOT_SWITCHER_KEY_FILE";

    // The variable names a file, never the key itself, so the secret stays out of the
    // environment block and the command line. A set variable is an explicit request: a missing
    // file or a non-loopback server is an error, not a silent fallback to another identity.
    // True when every address the host name resolves to is loopback or belongs to a local
    // network interface.
    internal static bool IsThisMachine(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : Dns.GetHostAddresses(host);
            if (addresses.Length == 0) return false;
            var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .ToHashSet();
            return addresses.All(address => IPAddress.IsLoopback(address) || local.Contains(address));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return false;
        }
    }

    internal static string? ReadSwitcherKey(string? path, string? server)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // https only: a plain-http listener on a free loopback port could be any local user's
        // process, and TLS validation is what proves the peer is the NodePilot service.
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsThisMachine(uri.Host))
            throw new InvalidOperationException(
                "The Switcher key is only sent over https to a server address of this machine.");

        string key;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            key = reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The Switcher key file could not be read: {ex.Message}");
        }

        if (key.Length == 0)
            throw new InvalidOperationException("The Switcher key file is empty.");
        return key;
    }
}
