using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Controllers;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> for pipeline-level smoke tests
/// that exercise the real Program.cs middleware chain (authentication, authorization, CSRF,
/// rate limiting, ProblemDetails normalization) instead of instantiating controllers
/// directly. Boots the host against an in-memory SQLite database with background services
/// removed, and seeds/logs in local users through the real <c>/api/auth/login</c> endpoint.
/// </summary>
public sealed class ApiPipelineFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nodepilot-api-pipeline-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Jwt:Key must be set via UseSetting: AddNodePilotAuthentication resolves the signing
        // key before builder.Build() runs, so ConfigureAppConfiguration values aren't visible
        // yet. Skipping this makes JwtKeyResolver fall back to writing jwt-secret.key, which
        // fails the ACL check on a fresh CI checkout.
        builder.UseSetting("Jwt:Key", "NodePilot-Test-Jwt-Key-For-Pipeline-Smoke-32-Bytes");

        // Same pre-Build visibility trap as Jwt:Key: AddNodePilotAuthentication captures
        // ActiveAuthenticationConfiguration at registration time, so LocalLoginMode is frozen
        // before ConfigureAppConfiguration replays. Pin it here so the smoke layer's seeded
        // users keep logging in even if the appsettings.Development.json default changes.
        builder.UseSetting("Authentication:LocalLoginMode", "Enabled");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            Directory.CreateDirectory(_tempDir);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AdminSetupTokenPath"] = Path.Combine(_tempDir, "admin-setup.token"),
                ["Logging:SupportLog:Enabled"] = "false",
                ["Logging:SupportLog:DbProjectionEnabled"] = "false",
                ["Retention:Executions:Enabled"] = "false",
                ["Retention:AuditLog:Enabled"] = "false",
                ["Retention:SupportEvents:Enabled"] = "false",
                ["OpenTelemetry:Enabled"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            RemoveDbContextServices(services);
            services.RemoveAll<IHostedService>();

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            services.AddDbContext<NodePilotDbContext>(options =>
            {
                options.UseSqlite(_connection);
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            });
        });
    }

    /// <summary>
    /// Seeds a local user directly into the database, mirroring what
    /// <c>UsersController.Create</c> persists: the same BCrypt hash that
    /// <c>AuthController.Login</c> verifies, plus the default Root-folder grant every
    /// non-Admin receives (Operator gets FolderEditor, Viewer gets FolderViewer). Without it,
    /// folder RBAC would block an Operator's workflow mutations in the role-matrix tests.
    /// </summary>
    public async Task<Guid> CreateUserAsync(string username, string password, UserRole role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, AuthController.BCryptWorkFactor),
            Role = role,
            Provider = AuthProvider.Local,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            // Backdated one minute: TokenValidityMiddleware rejects any token whose issue
            // time predates PasswordChangedAt (H-3 password-change invalidation). The token
            // carries millisecond precision (np_iat_ms), so "now" would usually pass — the
            // slack simply removes any clock-edge flakiness between seed and login.
            PasswordChangedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.Users.Add(user);

        if (role != UserRole.Admin)
        {
            db.SharedFolderPermissions.Add(new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = SharedWorkflowFolder.RootFolderId,
                PrincipalType = FolderPrincipalType.User,
                PrincipalKey = user.Id.ToString("D"),
                Role = role == UserRole.Operator
                    ? SharedFolderRole.FolderEditor
                    : SharedFolderRole.FolderViewer,
                GrantedAt = DateTime.UtcNow,
                GrantedByUserId = null,
            });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Logs in through the real <c>POST /api/auth/login</c> endpoint. The response sets the
    /// httpOnly <c>np_auth</c> JWT cookie plus the JS-readable <c>np_csrf</c> token cookie
    /// (see AuthSessionIssuer.SetAuthCookies), so <paramref name="client"/> stays authenticated
    /// for later requests. The CSRF token is returned separately because CsrfMiddleware needs
    /// it echoed back in the <c>X-CSRF-Token</c> header on every mutating request.
    /// </summary>
    public static async Task<AuthenticatedSession> LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { username, password });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"login for seeded user '{username}' must succeed — if this fails, the smoke " +
            "factory's LocalLoginMode/seeding contract with AuthController drifted");

        // The CSRF token only travels as a Set-Cookie value, which ResponseCookies.Append
        // escapes (it is Base64, so it can contain '+', '/', '='). CsrfMiddleware compares
        // the header against the unescaped value, so unescape here to match.
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        var csrfCookie = setCookies.FirstOrDefault(v =>
            v.StartsWith(AuthController.CsrfCookieName + "=", StringComparison.Ordinal));
        csrfCookie.Should().NotBeNull(
            $"login must set the {AuthController.CsrfCookieName} cookie; got: {string.Join(" | ", setCookies)}");
        var rawValue = csrfCookie![(AuthController.CsrfCookieName.Length + 1)..].Split(';')[0];
        var csrfToken = Uri.UnescapeDataString(rawValue);

        var identity = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AuthenticatedSession(
            identity.GetProperty("userId").GetGuid(),
            identity.GetProperty("role").GetString()!,
            csrfToken);
    }

    /// <summary>Result of <see cref="LoginAsync"/>: identity facts + the CSRF header
    /// value.</summary>
    public sealed record AuthenticatedSession(Guid UserId, string Role, string CsrfToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection?.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static void RemoveDbContextServices(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (ReferencesNodePilotDbContext(services[i].ServiceType))
                services.RemoveAt(i);
        }
    }

    private static bool ReferencesNodePilotDbContext(Type serviceType)
    {
        if (serviceType == typeof(NodePilotDbContext)
            || serviceType == typeof(DbContextOptions<NodePilotDbContext>))
            return true;

        return serviceType.IsGenericType
               && serviceType.GenericTypeArguments.Any(a => a == typeof(NodePilotDbContext));
    }
}
