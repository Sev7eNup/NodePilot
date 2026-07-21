using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class SqlActivityTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connStr;

    public SqlActivityTests()
    {
        // Shared in-memory SQLite. The connection must stay open for the
        // schema/data to survive across SqlActivity's own connection.
        _connStr = $"Data Source=SqlActivityTests_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connStr);
        _keepAlive.Open();

        using var seed = _keepAlive.CreateCommand();
        seed.CommandText = """
            CREATE TABLE Widgets (Id INTEGER PRIMARY KEY, Name TEXT, Price REAL);
            INSERT INTO Widgets (Name, Price) VALUES ('Widget A', 9.99), ('Widget B', 19.50);
        """;
        seed.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>Explicit dev compatibility mode — raw connection strings are permitted.</summary>
    private IConfiguration DefaultConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlActivity:RequireConnectionRef"] = "false",
        })
        .Build();

    /// <summary>Opts into the strict mode that forces every workflow onto a named whitelist.</summary>
    private IConfiguration RequireRefConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlActivity:RequireConnectionRef"] = "true",
        })
        .Build();

    /// <summary>Config where the connection string is whitelisted under a name.</summary>
    private IConfiguration NamedConnectionConfig(string refName) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"SqlActivity:ConnectionStrings:{refName}"] = _connStr,
        })
        .Build();

    private SqlActivity ActDefault() => new(DefaultConfig());
    private SqlActivity ActRequireRef() => new(RequireRefConfig());
    private SqlActivity ActWithNamed(string refName) => new(NamedConnectionConfig(refName));

    private JsonElement Cfg(string query, string provider = "sqlite") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { connectionString = _connStr, provider, query })).RootElement;

    private JsonElement CfgRef(string refName, string query, string provider = "sqlite", object? parameters = null) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { connectionRef = refName, provider, query, parameters })).RootElement;

    private static StepExecutionContext Ctx() => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "sql-1" };

    [Fact]
    public async Task Select_ReturnsRowsAsJsonAndOutputParameters()
    {
        var result = await ActDefault().ExecuteAsync(Ctx(),
            Cfg("SELECT Id, Name, Price FROM Widgets ORDER BY Id"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Widget A").And.Contain("Widget B");
        result.OutputParameters["rowCount"].Should().Be("2");
        result.OutputParameters["Name"].Should().Be("Widget A");
        result.OutputParameters["row0_Name"].Should().Be("Widget A");
        result.OutputParameters["row1_Name"].Should().Be("Widget B");
    }

    [Fact]
    public async Task Insert_ReportsRowsAffected()
    {
        var result = await ActDefault().ExecuteAsync(Ctx(),
            Cfg("INSERT INTO Widgets (Name, Price) VALUES ('Widget C', 42), ('Widget D', 43)"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["rowsAffected"].Should().Be("2");
        result.Output.Should().Contain("rowsAffected=2");
    }

    [Fact]
    public async Task RawConnectionString_Allowed_WhenRequireConnectionRefIsExplicitlyFalse()
    {
        var result = await ActDefault().ExecuteAsync(Ctx(), Cfg("SELECT 1 AS one"), CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["one"].Should().Be("1");
    }

    [Fact]
    public async Task RawConnectionString_Rejected_WhenRequireConnectionRefIsMissing()
    {
        var act = new SqlActivity(new ConfigurationBuilder().Build());

        var result = await act.ExecuteAsync(Ctx(), Cfg("SELECT 1"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("named connectionRef");
    }

    [Fact]
    public async Task RawConnectionString_Rejected_WhenRequireConnectionRefIsOn()
    {
        // Strict mode: SqlActivity:RequireConnectionRef=true forces workflows onto the whitelist.
        var result = await ActRequireRef().ExecuteAsync(Ctx(), Cfg("SELECT 1"), CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("named connectionRef");
    }

    [Fact]
    public async Task ConnectionRef_UsesWhitelistEntry()
    {
        var result = await ActWithNamed("widgets").ExecuteAsync(Ctx(),
            CfgRef("widgets", "SELECT COUNT(*) AS n FROM Widgets"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["n"].Should().Be("2");
    }

    [Fact]
    public async Task ConnectionRef_UnknownName_Rejects()
    {
        var result = await ActWithNamed("widgets").ExecuteAsync(Ctx(),
            CfgRef("not-whitelisted", "SELECT 1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not configured");
    }

    [Fact]
    public async Task Parameters_AreBoundNotInterpolated()
    {
        // A literal single-quote inside the parameter would break a string-interpolated
        // query; parameterization makes it land safely as a value.
        var act = ActWithNamed("widgets");
        var attack = "' OR 1=1; --";
        var result = await act.ExecuteAsync(Ctx(),
            CfgRef("widgets", "SELECT * FROM Widgets WHERE Name = $name",
                parameters: new Dictionary<string, object?> { ["name"] = attack }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["rowCount"].Should().Be("0", "the attack string must not match any real row");
    }

    [Fact]
    public async Task MissingQuery_ReturnsFailure()
    {
        var act = ActDefault();
        var cfg = JsonDocument.Parse($$"""{"connectionString":"{{_connStr}}","provider":"sqlite"}""").RootElement;
        var result = await act.ExecuteAsync(Ctx(), cfg, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("query");
    }

    [Fact]
    public async Task InvalidSql_ReturnsFailure()
    {
        var result = await ActDefault().ExecuteAsync(Ctx(),
            Cfg("SELECT * FROM NoSuchTable"), CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("SQL error");
    }

    [Fact]
    public async Task Builder_Sqlite_DataSource_ConnectsViaBuiltString()
    {
        // Builder-mode end-to-end for SQLite: write a real temp file (a shared in-memory DB
        // needs 'Mode=Memory;Cache=Shared' in the connection string, which the builder
        // deliberately doesn't add — this simulates the realistic case of a file-backed DB).
        var path = Path.Combine(Path.GetTempPath(), $"nodepilot-sqlbuilder-{Guid.NewGuid():N}.db");
        try
        {
            using (var seedConn = new SqliteConnection($"Data Source={path}"))
            {
                seedConn.Open();
                using var cmd = seedConn.CreateCommand();
                cmd.CommandText = "CREATE TABLE T (Id INTEGER); INSERT INTO T VALUES (1), (2), (3);";
                cmd.ExecuteNonQuery();
            }

            var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                provider = "sqlite",
                dataSource = path,
                query = "SELECT COUNT(*) AS n FROM T",
            })).RootElement;

            var result = await ActDefault().ExecuteAsync(Ctx(), cfg, CancellationToken.None);
            result.Success.Should().BeTrue();
            result.OutputParameters["n"].Should().Be("3");
        }
        finally
        {
            // The SQLite connection pool would otherwise keep the file open → Delete would fail on Windows.
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Builder_SqlServer_BuildsExpectedConnectionString()
    {
        // No live SQL Server in tests — we verify via the generated connection string that
        // SqlConnectionStringBuilder picks up the fields and the defaults are correct.
        var (cs, err) = BuildViaActivity(new
        {
            provider = "sqlserver",
            server = "db01\\SQLEXPRESS",
            database = "Reporting",
            // Defaults: authentication=integrated, encrypt=true, trustServerCertificate=false
        });

        err.Should().BeNull();
        var b = new SqlConnectionStringBuilder(cs);
        b.DataSource.Should().Be("db01\\SQLEXPRESS");
        b.InitialCatalog.Should().Be("Reporting");
        b.IntegratedSecurity.Should().BeTrue();
        b.Encrypt.Should().Be(SqlConnectionEncryptOption.Mandatory);
        b.TrustServerCertificate.Should().BeFalse();
    }

    [Fact]
    public void Builder_SqlServer_SqlAuth_RequiresUsername()
    {
        var (cs, err) = BuildViaActivity(new
        {
            provider = "sqlserver",
            server = "db01",
            database = "App",
            authentication = "sql",
        });

        cs.Should().BeNull();
        err.Should().Contain("username");
    }

    [Fact]
    public void Builder_SqlServer_SqlAuth_EmbedsCredentials()
    {
        // Passwords with special characters must be escaped correctly by the builder —
        // otherwise the connection string breaks on an apostrophe or semicolon.
        var (cs, err) = BuildViaActivity(new
        {
            provider = "sqlserver",
            server = "db01",
            database = "App",
            authentication = "sql",
            username = "svc-app",
            password = "p@ss;\"word'",
            encrypt = false,
            trustServerCertificate = true,
        });

        err.Should().BeNull();
        var b = new SqlConnectionStringBuilder(cs);
        b.IntegratedSecurity.Should().BeFalse();
        b.UserID.Should().Be("svc-app");
        b.Password.Should().Be("p@ss;\"word'");
        b.Encrypt.Should().Be(SqlConnectionEncryptOption.Optional);
        b.TrustServerCertificate.Should().BeTrue();
    }

    [Fact]
    public void Builder_SqlServer_MissingServer_Rejects()
    {
        var (cs, err) = BuildViaActivity(new { provider = "sqlserver", database = "App" });
        cs.Should().BeNull();
        err.Should().Contain("server");
    }

    [Fact]
    public void Builder_Postgres_BuildsExpectedConnectionString()
    {
        var (cs, err) = BuildViaActivity(new
        {
            provider = "postgres",
            host = "pg01.example.com",
            port = 5433,
            database = "analytics",
            username = "reader",
            password = "secret;1",
            sslMode = "Require",
        });

        err.Should().BeNull();
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        b.Host.Should().Be("pg01.example.com");
        b.Port.Should().Be(5433);
        b.Database.Should().Be("analytics");
        b.Username.Should().Be("reader");
        b.Password.Should().Be("secret;1");
        b.SslMode.Should().Be(Npgsql.SslMode.Require);
    }

    [Fact]
    public void Builder_Postgres_DefaultSslMode_IsRequire()
    {
        // M-4 (security audit 2026-05-15): when the author does not specify sslMode the builder
        // must default to Require, NOT Npgsql's built-in Prefer. Prefer silently downgrades to a
        // plaintext connection when the server doesn't offer TLS, exposing DB credentials to a
        // MITM. The secure default forces an encrypted channel.
        var (cs, err) = BuildViaActivity(new
        {
            provider = "postgres",
            host = "pg01.example.com",
            database = "analytics",
            username = "reader",
            password = "secret;1",
        });

        err.Should().BeNull();
        new Npgsql.NpgsqlConnectionStringBuilder(cs).SslMode.Should().Be(Npgsql.SslMode.Require);
    }

    [Fact]
    public void Builder_Postgres_ExplicitSslModeDisable_IsHonoured()
    {
        // The secure default is overridable: an operator on a trusted local socket can still opt
        // into plaintext by setting sslMode explicitly. Only the *implicit* default changed.
        var (cs, err) = BuildViaActivity(new
        {
            provider = "postgres",
            host = "localhost",
            database = "analytics",
            sslMode = "Disable",
        });

        err.Should().BeNull();
        new Npgsql.NpgsqlConnectionStringBuilder(cs).SslMode.Should().Be(Npgsql.SslMode.Disable);
    }

    [Fact]
    public void Builder_Postgres_MissingHost_Rejects()
    {
        var (cs, err) = BuildViaActivity(new { provider = "postgres", database = "analytics" });
        cs.Should().BeNull();
        err.Should().Contain("host");
    }

    [Fact]
    public void Builder_Sqlite_MissingDataSource_Rejects()
    {
        // If 'dataSource' is neither set nor empty, resolution falls through to the raw/ref
        // branch — only an explicit, empty 'dataSource' should trigger this path. Here we
        // test the case where neither the builder nor the raw path applies: then the
        // generic "provide one of ..." message must come back.
        var (cs, err) = BuildViaActivity(new { provider = "sqlite" });
        cs.Should().BeNull();
        err.Should().Contain("connectionRef");
    }

    [Fact]
    public void Builder_TakesPrecedenceOverRawConnectionString()
    {
        // When both are set, the builder wins — a leftover raw 'connectionString' from an
        // earlier edit must not override the newly configured server.
        var (cs, err) = BuildViaActivity(new
        {
            provider = "sqlserver",
            server = "from-builder",
            database = "App",
            connectionString = "Server=from-raw;Database=Other;Integrated Security=True",
        });

        err.Should().BeNull();
        cs.Should().Contain("from-builder").And.NotContain("from-raw");
    }

    [Fact]
    public void Builder_ConnectionRef_StillTakesPrecedence()
    {
        // connectionRef stays the highest priority — even if builder fields are also set,
        // the whitelisted entry wins (otherwise a workflow could bypass the whitelist
        // protection by additionally setting 'server').
        var act = new SqlActivity(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:ConnectionStrings:trusted"] = "Server=from-ref;Database=Trusted;Integrated Security=True",
            })
            .Build());

        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "sqlserver",
            connectionRef = "trusted",
            server = "attacker-controlled",
            query = "SELECT 1",
        })).RootElement;

        var (cs, err) = InvokeResolveConnectionString(act, cfg, "sqlserver");
        err.Should().BeNull();
        cs.Should().Contain("from-ref").And.NotContain("attacker-controlled");
    }

    /// <summary>
    /// Helper: calls the private <c>ResolveConnectionString</c> method via reflection on a
    /// fresh <see cref="SqlActivity"/> so we can test the connection-string builder without
    /// a live DB. Reflection is fine here — the test is already tied to the internal
    /// contract shape, and the builder methods are the only surface area being exercised.
    /// </summary>
    private static (string? ConnStr, string? Error) BuildViaActivity(object configObject)
    {
        var act = new SqlActivity(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:RequireConnectionRef"] = "false",
            })
            .Build());
        var json = JsonDocument.Parse(JsonSerializer.Serialize(configObject)).RootElement;
        var provider = json.TryGetProperty("provider", out var p) ? p.GetString() ?? "sqlserver" : "sqlserver";
        return InvokeResolveConnectionString(act, json, provider.ToLowerInvariant());
    }

    private static (string? ConnStr, string? Error) InvokeResolveConnectionString(
        SqlActivity act, JsonElement config, string provider)
    {
        var method = typeof(SqlActivity).GetMethod(
            "ResolveConnectionString",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var result = method.Invoke(act, new object[] { config, provider });
        var tuple = (System.Runtime.CompilerServices.ITuple)result!;
        return ((string?)tuple[0], (string?)tuple[1]);
    }

    // ---- Strict-mode: builder-with-credentials must also be blocked ----

    [Fact]
    public async Task RequireConnectionRef_BlocksBuilderWithPassword_Postgres()
    {
        // Phase-3 hardening tightening: when SqlActivity:RequireConnectionRef=true the
        // policy is "no DB secrets in workflow JSON". Accepting builder mode with a
        // password field would defeat that — the secret would still live in the workflow
        // definition / version snapshot / export.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "postgres",
            host = "pg.example.com",
            database = "analytics",
            username = "reader",
            password = "s3cret",
            query = "SELECT 1",
        })).RootElement;

        var result = await ActRequireRef().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("named connectionRef");
        result.ErrorOutput.Should().Contain("credentials");
    }

    [Fact]
    public async Task RequireConnectionRef_BlocksBuilderWithUsername_OnlyNoPassword()
    {
        // The credential check is OR over username/password — a workflow that supplies just
        // a username intends to ride on a templated password downstream, which still ends
        // up in the workflow JSON eventually.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "postgres",
            host = "pg.example.com",
            username = "reader",
            query = "SELECT 1",
        })).RootElement;

        var result = await ActRequireRef().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("named connectionRef");
    }

    [Fact]
    public async Task RequireConnectionRef_BlocksBuilderWithSqlAuthEvenWithoutPassword()
    {
        // The SQL Server "authentication=sql" knob signals an intention to use a templated
        // credential — that payload would carry the password downstream. Block it on the
        // same grounds as an explicit password field.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "sqlserver",
            server = "db01",
            database = "App",
            authentication = "sql",
            query = "SELECT 1",
        })).RootElement;

        var result = await ActRequireRef().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("named connectionRef");
    }

    [Fact]
    public void RequireConnectionRef_AllowsBuilderWithIntegratedAuth()
    {
        // Integrated SQL auth (Windows Auth) carries no secret in the workflow JSON.
        // Strict mode lets it through because there's nothing to leak — the credential
        // is the service-account token resolved at connect time.
        var act = new SqlActivity(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:RequireConnectionRef"] = "true",
            })
            .Build());

        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "sqlserver",
            server = "db01",
            database = "App",
            // No username, no password, no explicit authentication — defaults to integrated.
            query = "SELECT 1",
        })).RootElement;

        var (cs, err) = InvokeResolveConnectionString(act, cfg, "sqlserver");

        err.Should().BeNull("integrated auth carries no secret in the workflow JSON");
        cs.Should().Contain("Integrated Security=True");
    }

    // ---- H-1 (security audit 2026-05-15): query must not carry {{...}} templates ----

    [Fact]
    public async Task Query_WithUnresolvedTemplate_FailsCleanly()
    {
        // The engine routes sql.query around the {{var}} resolver, so any template that
        // arrives here is either an author mistake or a code-path that forgot to opt into
        // the field guard. Either way: refuse before opening a connection, never substitute
        // attacker-controlled values into raw CommandText.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connectionString = _connStr,
            provider = "sqlite",
            query = "SELECT * FROM Widgets WHERE Id = {{manual.id}}",
        })).RootElement;

        var result = await ActDefault().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("{{...}} templates");
        result.ErrorOutput.Should().Contain("parameters");
    }

    [Fact]
    public async Task Query_WithGlobalTemplate_AlsoFails()
    {
        // Globals look "trusted" but the guard is symmetric — an admin-managed value smuggled
        // into raw SQL is still raw SQL. The author must use a bound parameter.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connectionString = _connStr,
            provider = "sqlite",
            query = "SELECT * FROM Widgets WHERE Name = {{globals.WIDGET_NAME}}",
        })).RootElement;

        var result = await ActDefault().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("{{...}} templates");
    }

    [Fact]
    public async Task Parameters_CanCarryTemplate_AndBindSafely()
    {
        // The 'parameters' object is intentionally NOT field-guarded — values there get
        // standard template expansion and then ride as ADO.NET parameters, which is the
        // safe path. This regression test pins down that the guard's scope is just `query`.
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connectionString = _connStr,
            provider = "sqlite",
            query = "SELECT * FROM Widgets WHERE Name = $name",
            parameters = new Dictionary<string, object?> { ["name"] = "Widget A" },
        })).RootElement;

        var result = await ActDefault().ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["rowCount"].Should().Be("1");
    }

    [Fact]
    public void RequireConnectionRef_AllowsBuilderWithSqliteFilePath()
    {
        // SQLite builder is just a file path — no credential on the wire, no secret in
        // the workflow JSON. Strict mode permits it for the same reason as integrated auth.
        var act = new SqlActivity(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:RequireConnectionRef"] = "true",
            })
            .Build());

        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "sqlite",
            dataSource = "C:\\data\\app.db",
            query = "SELECT 1",
        })).RootElement;

        var (cs, err) = InvokeResolveConnectionString(act, cfg, "sqlite");

        err.Should().BeNull();
        cs.Should().Contain("app.db");
    }
}
