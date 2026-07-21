using System.Runtime.Versioning;
using NodePilot.Cli.Settings;

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
            && !SessionContext.HasSameServerOrigin(session.Server, server))
        {
            session = null;
        }

        return new SessionContext
        {
            Profile = profile,
            Server = server,
            Session = session,
            AllowInsecureLoopback = settings.AllowInsecureLoopback,
        };
    }
}
