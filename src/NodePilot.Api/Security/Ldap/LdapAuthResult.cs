namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// What an LDAP simple-bind returns when the credentials were valid. Carried back to the
/// auth pipeline so the user-mapping step (<see cref="ExternalUserMapper"/>) can
/// JIT-provision (or update) the matching <c>User</c> row.
/// </summary>
/// <param name="ExternalId">Stable directory identifier — for AD this is the user's
/// <c>objectGUID</c> (canonical 36-char Guid string). The login path keys JIT lookup on
/// <c>(Provider, ExternalId)</c> so a renamed AD user keeps the same NodePilot row.</param>
/// <param name="Upn">The UPN that was bound — useful for audit + display.</param>
/// <param name="DisplayName">User's friendly name from <c>displayName</c> / <c>cn</c>;
/// falls back to the UPN local part when the directory has neither set.</param>
/// <param name="GroupSids">Transitive group SIDs from AD's <c>tokenGroups</c> attribute.
/// Empty list when the user has no group memberships visible to the bind context.</param>
/// <param name="LegacyExternalId">Previous provider-specific identifier. LDAP supplies
/// objectGUID so an unambiguous pre-migration row can be upgraded in place. It is never
/// used to merge two different users.</param>
public sealed record LdapAuthResult(
    string ExternalId,
    string Upn,
    string DisplayName,
    IReadOnlyList<string> GroupSids,
    string? LegacyExternalId = null)
{
    /// <summary>
    /// Canonical subject within the external identity authority. For Active Directory this
    /// is the user objectSid for both LDAP and Windows Negotiate. ExternalId remains as a
    /// compatibility alias for callers compiled against the earlier result shape.
    /// </summary>
    public string Subject => ExternalId;
}
