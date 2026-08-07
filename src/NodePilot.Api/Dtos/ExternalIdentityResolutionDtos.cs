using System.Text.Json.Serialization;
using NodePilot.Core.Enums;

namespace NodePilot.Api.Dtos;

/// <summary>
/// DTOs for the Admin-only <c>api/users/*-conflict</c> identity-repair endpoints
/// (<c>ExternalIdentityResolutionController</c>). Moved out of the controller file so the
/// path-scanning <c>ApiDtoParityTests</c> discovery sees them like every other contract type.
/// </summary>
public sealed record ResolveAdIdentityConflictRequest(
    string CanonicalSid,
    string LegacyLdapObjectGuid,
    [property: JsonRequired] Guid WinnerUserId);

public sealed record ResolveUpgradeIdentityConflictRequest(
    [property: JsonRequired] AuthProvider Provider,
    string ConflictExternalId,
    [property: JsonRequired] Guid WinnerUserId,
    IReadOnlyCollection<Guid> LoserUserIds);

public sealed record UpgradeIdentityConflictCandidate(
    Guid Id,
    string Username,
    UserRole Role,
    bool IsActive,
    bool IsTombstoned);

public sealed record UpgradeIdentityConflict(
    AuthProvider Provider,
    string ConflictExternalId,
    IReadOnlyList<UpgradeIdentityConflictCandidate> Candidates);
