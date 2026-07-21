using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security.Oidc;

/// <summary>
/// HA-safe server-side store for the short-lived external OIDC authentication ticket.
/// CookieAuthentication serializes only the random handle; the protected principal (and
/// potentially hundreds of group claims) remains in the shared application database.
/// </summary>
public sealed class OidcTicketStore : ITicketStore
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;

    public OidcTicketStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider)
    {
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector("NodePilot.OidcTicketStore.v1");
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreCoreAsync(ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        RenewCoreAsync(key, ticket, CancellationToken.None);

    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        RetrieveCoreAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key) => RemoveCoreAsync(key, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context) =>
        StoreCoreAsync(ticket, context.RequestAborted);

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext context) =>
        RenewCoreAsync(key, ticket, context.RequestAborted);

    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext context) =>
        RetrieveCoreAsync(key, context.RequestAborted);

    public Task RemoveAsync(string key, HttpContext context) =>
        RemoveCoreAsync(key, context.RequestAborted);

    private async Task<string> StoreCoreAsync(AuthenticationTicket ticket, CancellationToken ct)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        db.OidcLoginTickets.Add(new OidcLoginTicket
        {
            Id = key,
            ProtectedPayload = Protect(ticket),
            ExpiresAt = Expiration(ticket),
        });
        await db.SaveChangesAsync(ct);
        return key;
    }

    private async Task RenewCoreAsync(string key, AuthenticationTicket ticket, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var row = await db.OidcLoginTickets.SingleOrDefaultAsync(x => x.Id == key, ct);
        if (row is null) return;
        row.ProtectedPayload = Protect(ticket);
        row.ExpiresAt = Expiration(ticket);
        await db.SaveChangesAsync(ct);
    }

    private async Task<AuthenticationTicket?> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var row = await db.OidcLoginTickets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == key, ct);
        if (row is null) return null;

        // External handoff tickets are one-time credentials. Competing callback requests
        // may both read the row, but exactly one can win this atomic DELETE; losers receive
        // no principal and therefore cannot mint an additional NodePilot session.
        var consumed = await db.OidcLoginTickets
            .Where(x => x.Id == key)
            .ExecuteDeleteAsync(ct);
        if (consumed != 1 || row.ExpiresAt <= DateTime.UtcNow) return null;
        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(row.ProtectedPayload));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task RemoveCoreAsync(string key, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await db.OidcLoginTickets.Where(x => x.Id == key).ExecuteDeleteAsync(ct);
    }

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private static DateTime Expiration(AuthenticationTicket ticket)
    {
        var maximum = DateTimeOffset.UtcNow.Add(MaximumLifetime);
        var requested = ticket.Properties.ExpiresUtc ?? maximum;
        return (requested < maximum ? requested : maximum).UtcDateTime;
    }
}
