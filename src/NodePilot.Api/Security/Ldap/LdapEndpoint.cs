namespace NodePilot.Api.Security.Ldap;

internal sealed record LdapEndpoint(string Host, int Port)
{
    public static IReadOnlyList<LdapEndpoint> Resolve(LdapOptions options) =>
        Resolve(options.Endpoints, options.Server, options.Port);

    public static IReadOnlyList<LdapEndpoint> Resolve(
        IEnumerable<string>? configured,
        string? legacyServer,
        int legacyPort)
    {
        var raw = configured?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList() ?? [];
        if (raw.Count == 0 && !string.IsNullOrWhiteSpace(legacyServer))
            raw.Add($"{legacyServer.Trim()}:{legacyPort}");

        var result = new List<LdapEndpoint>();
        foreach (var value in raw)
        {
            var withScheme = value.Contains("://", StringComparison.Ordinal)
                ? value
                : "ldaps://" + value;
            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
                throw Invalid(value);
            var port = uri.Port == -1 ? 636 : uri.Port;
            if (!uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(uri.Host)
                || port is < 1 or > 65535
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw Invalid(value);
            }
            result.Add(new LdapEndpoint(uri.Host, port));
        }
        return result;
    }

    private static LdapInfrastructureException Invalid(string value) => new(
        $"Invalid LDAP endpoint '{value}'. Use a host, host:port, or ldaps://host:port value.");
}
