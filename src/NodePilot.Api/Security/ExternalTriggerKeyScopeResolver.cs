using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;

namespace NodePilot.Api.Security;

/// <summary>
/// The authorization scope attached to one successfully authenticated external-trigger key.
/// Integration ids are operational labels only; keys are persisted as SHA-256 hashes.
/// </summary>
internal sealed record ExternalTriggerKeyScope(
    string IntegrationId,
    string PrincipalId,
    IReadOnlySet<Guid> AllowedWorkflowIds);

/// <summary>
/// Authenticates external-trigger keys and returns their GUID-only workflow scope.
/// Every configured hash is compared on every request. Duplicate matches, malformed hashes,
/// and malformed workflow ids fail closed instead of silently widening a key's authority.
/// </summary>
internal static class ExternalTriggerKeyScopeResolver
{
    private const int Sha256Length = 32;
    private const string HashedKeysPath = "ExternalTrigger:Keys";

    private sealed record HashedKeyDefinition(
        string IntegrationId,
        string? KeyHash,
        IReadOnlySet<Guid> AllowedWorkflowIds);

    public static ExternalTriggerKeyScope? Authenticate(
        IConfiguration configuration,
        string? presentedKey,
        int minimumKeyBytes)
    {
        if (string.IsNullOrWhiteSpace(presentedKey)
            || Encoding.UTF8.GetByteCount(presentedKey) < minimumKeyBytes)
        {
            return null;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        var presentedHash = SHA256.HashData(presentedBytes);
        CryptographicOperations.ZeroMemory(presentedBytes);
        ExternalTriggerKeyScope? matchedScope = null;
        var matchCount = 0;
        var hashedKeysAreValid = TryReadHashedKeyDefinitions(
            configuration, out var hashedKeyDefinitions);
        var invalidConfiguration = !hashedKeysAreValid;

        foreach (var definition in hashedKeyDefinitions)
        {
            var configuredHash = new byte[Sha256Length];
            var hashIsValid = TryDecodeSha256(definition.KeyHash, configuredHash);

            // Compare even invalid entries against a fixed zero buffer. This keeps the loop shape
            // independent of which configured entry is malformed and avoids an early-match oracle.
            var matches = CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
            if (!hashIsValid)
                invalidConfiguration = true;

            if (!hashIsValid)
            {
                continue;
            }

            if (!matches)
                continue;

            matchCount++;
            matchedScope = new ExternalTriggerKeyScope(
                definition.IntegrationId,
                BuildPrincipalId(definition.IntegrationId, presentedHash),
                definition.AllowedWorkflowIds);
        }

        // Transitional support for the former single plaintext key. It is no longer an
        // instance-wide capability: without an explicit GUID allow-list it authenticates to an
        // empty scope and therefore cannot execute any workflow. New installations should use
        // ExternalTrigger:Keys and store only hashes.
        var legacyKey = configuration["ExternalTrigger:ApiKey"];
        if (!string.IsNullOrWhiteSpace(legacyKey)
            && Encoding.UTF8.GetByteCount(legacyKey) >= minimumKeyBytes)
        {
            var legacyScopeIsValid = ProviderAtomicGuidList.TryRead(
                configuration,
                "ExternalTrigger:AllowedWorkflowIds",
                out var workflowIds);
            if (!legacyScopeIsValid)
                invalidConfiguration = true;

            var legacyMatches = SecretComparer.FixedTimeEquals(presentedKey, legacyKey);
            if (legacyMatches)
            {
                matchCount++;
                if (legacyScopeIsValid)
                    matchedScope = new ExternalTriggerKeyScope(
                        "legacy",
                        BuildPrincipalId("legacy", presentedHash),
                        workflowIds);
            }
        }

        CryptographicOperations.ZeroMemory(presentedHash);

        // Reusing one key in multiple integration entries is ambiguous: unioning scopes would
        // grant more access than either entry declares, while choosing one is order-dependent.
        return !invalidConfiguration && matchCount == 1 ? matchedScope : null;
    }

    /// <summary>
    /// Reads the complete hashed-key map from exactly one configuration provider. Microsoft's
    /// merged configuration view is additive for dictionaries: a higher-priority <c>Keys: {}</c>
    /// otherwise leaves every lower-provider integration visible. That makes emergency key
    /// revocation appear successful while the old credential remains usable. The highest provider
    /// which declares either the map or one of its children therefore owns the whole snapshot.
    /// Empty objects/null values are deny-all tombstones; partial entries fail closed rather than
    /// inheriting a hash or scope from another provider.
    /// </summary>
    private static bool TryReadHashedKeyDefinitions(
        IConfiguration configuration,
        out IReadOnlyList<HashedKeyDefinition> definitions)
    {
        if (configuration is not IConfigurationRoot root)
        {
            // Production uses IConfigurationRoot/ConfigurationManager. An opaque wrapper cannot
            // prove provider ownership, so accepting its merged dictionary would weaken the
            // authorization boundary this method exists to enforce.
            definitions = [];
            return false;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            var hasExactMapValue = provider.TryGet(HashedKeysPath, out var exactMapValue);
            var integrationIds = provider.GetChildKeys([], HashedKeysPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!hasExactMapValue && integrationIds.Length == 0)
                continue;

            // JsonConfigurationProvider represents an empty object with an exact null value.
            // A scalar plus children is ambiguous; a non-empty scalar is not a supported map.
            if (hasExactMapValue)
            {
                definitions = [];
                return integrationIds.Length == 0 && string.IsNullOrWhiteSpace(exactMapValue);
            }

            var parsed = new List<HashedKeyDefinition>(integrationIds.Length);
            foreach (var integrationId in integrationIds)
            {
                var entryPath = $"{HashedKeysPath}:{integrationId}";
                var hasExactEntryValue = provider.TryGet(entryPath, out var exactEntryValue);
                var entryChildren = provider.GetChildKeys([], entryPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // An explicit empty integration object is a per-entry tombstone. Non-empty
                // scalars and scalar/child mixtures are malformed and fail the whole map closed.
                if (hasExactEntryValue)
                {
                    if (entryChildren.Length != 0 || !string.IsNullOrWhiteSpace(exactEntryValue))
                    {
                        definitions = [];
                        return false;
                    }

                    continue;
                }

                if (!provider.TryGet($"{entryPath}:KeyHash", out var keyHash)
                    || string.IsNullOrWhiteSpace(keyHash)
                    || !ProviderAtomicGuidList.TryReadFromProvider(
                        provider,
                        $"{entryPath}:AllowedWorkflowIds",
                        out var workflowIds))
                {
                    definitions = [];
                    return false;
                }

                parsed.Add(new HashedKeyDefinition(integrationId, keyHash, workflowIds));
            }

            definitions = parsed;
            return true;
        }

        definitions = [];
        return true;
    }

    private static bool TryDecodeSha256(string? encoded, Span<byte> destination)
    {
        destination.Clear();
        return !string.IsNullOrWhiteSpace(encoded)
               && Convert.TryFromBase64String(encoded, destination, out var bytesWritten)
               && bytesWritten == Sha256Length;
    }

    private static string BuildPrincipalId(string integrationId, ReadOnlySpan<byte> keyHash)
    {
        const string domain = "nodepilot:external-trigger:key-principal:v1";
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        // IConfiguration keys are case-insensitive. Canonicalizing prevents a casing-only config
        // change from creating a new idempotency principal for the same integration.
        var integrationBytes = Encoding.UTF8.GetBytes(integrationId.ToUpperInvariant());
        var material = new byte[4 + domainBytes.Length + 4 + integrationBytes.Length + 4 + keyHash.Length];
        var offset = 0;
        WriteLengthPrefixed(domainBytes, material, ref offset);
        WriteLengthPrefixed(integrationBytes, material, ref offset);
        BinaryPrimitives.WriteInt32BigEndian(material.AsSpan(offset, 4), keyHash.Length);
        keyHash.CopyTo(material.AsSpan(offset + 4));

        try
        {
            return Convert.ToHexString(SHA256.HashData(material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(integrationBytes);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static void WriteLengthPrefixed(
        ReadOnlySpan<byte> value,
        Span<byte> destination,
        ref int offset)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, 4), value.Length);
        offset += 4;
        value.CopyTo(destination[offset..]);
        offset += value.Length;
    }

}
