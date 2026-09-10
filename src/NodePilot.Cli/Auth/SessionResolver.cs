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

        return new SessionContext
        {
            Profile = profile,
            Server = server,
            Session = session,
            AllowInsecureLoopback = settings.AllowInsecureLoopback,
            Tls = new ClientTlsOptions(pin.Value, ConfigStore.ResolveSkipTlsVerification(settings.InsecureTls)),
            TlsPinNotice = pin.IgnoredReason,
        };
    }
}
