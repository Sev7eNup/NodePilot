using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnterpriseSsoIdentityModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var storeTypes = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => new
                {
                    Guid = "uniqueidentifier", String32 = "nvarchar(32)",
                    String256 = "nvarchar(256)", String384 = "nvarchar(384)",
                    Boolean = "bit", DateTime = "datetime2", Integer = "int",
                },
                "Npgsql.EntityFrameworkCore.PostgreSQL" => new
                {
                    Guid = "uuid", String32 = "character varying(32)",
                    String256 = "character varying(256)", String384 = "character varying(384)",
                    Boolean = "boolean", DateTime = "timestamp with time zone", Integer = "integer",
                },
                "Microsoft.EntityFrameworkCore.Sqlite" => new
                {
                    Guid = "TEXT", String32 = "TEXT", String256 = "TEXT", String384 = "TEXT",
                    Boolean = "INTEGER", DateTime = "TEXT", Integer = "INTEGER",
                },
                _ => throw new NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };

            migrationBuilder.AddColumn<string>(
                name: "DirectorySyncStatus",
                table: "Users",
                type: storeTypes.String32,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBreakGlass",
                table: "Users",
                type: storeTypes.Boolean,
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTombstoned",
                table: "Users",
                type: storeTypes.Boolean,
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDirectorySyncAt",
                table: "Users",
                type: storeTypes.DateTime,
                nullable: true);

            // Preserve the emergency login path on upgrade. BreakGlassOnly is the new
            // enterprise default, so every existing active local admin must remain able to
            // authenticate after this migration. New bootstrap admins are marked by the API.
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql("""
                    UPDATE Users
                    SET IsBreakGlass = 1
                    WHERE Provider = 'Local' AND Role = 'Admin' AND IsActive = 1;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET "IsBreakGlass" = TRUE
                    WHERE "Provider" = 'Local' AND "Role" = 'Admin' AND "IsActive" = TRUE;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET "IsBreakGlass" = 1
                    WHERE "Provider" = 'Local' AND "Role" = 'Admin' AND "IsActive" = 1;
                    """);
            }

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    UserId = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    AuthenticationMethod = table.Column<string>(type: storeTypes.String32, maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false),
                    RevokedAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: true),
                    AuthorizationVersion = table.Column<int>(type: storeTypes.Integer, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryMemberships",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    GroupKey = table.Column<string>(type: storeTypes.String256, maxLength: 256, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryMemberships", x => new { x.UserId, x.GroupKey });
                    table.ForeignKey(
                        name: "FK_DirectoryMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    UserId = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    Authority = table.Column<string>(type: storeTypes.String384, maxLength: 384, nullable: false),
                    Subject = table.Column<string>(type: storeTypes.String384, maxLength: 384, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill only unambiguous compatibility rows. Existing Windows users already
            // store the AD SID and can become canonical immediately. Existing LDAP users
            // store objectGUID, so retain that under a legacy authority until the next
            // successful LDAP lookup supplies objectSid. Duplicate legacy keys are skipped:
            // the mapper detects and refuses them instead of choosing or merging a user.
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql("""
                    INSERT INTO ExternalIdentities (Id, UserId, Authority, Subject, CreatedAt, LastSeenAt)
                    SELECT u.Id, u.Id, 'urn:nodepilot:identity:active-directory', u.ExternalId, u.CreatedAt, SYSUTCDATETIME()
                    FROM Users u
                    WHERE u.Provider = 'Windows' AND u.ExternalId IS NOT NULL
                      AND (SELECT COUNT(*) FROM Users d WHERE d.Provider = 'Windows' AND d.ExternalId = u.ExternalId) = 1;

                    INSERT INTO ExternalIdentities (Id, UserId, Authority, Subject, CreatedAt, LastSeenAt)
                    SELECT u.Id, u.Id, 'urn:nodepilot:identity:legacy-ldap-object-guid', u.ExternalId, u.CreatedAt, SYSUTCDATETIME()
                    FROM Users u
                    WHERE u.Provider = 'Ldap' AND u.ExternalId IS NOT NULL
                      AND (SELECT COUNT(*) FROM Users d WHERE d.Provider = 'Ldap' AND d.ExternalId = u.ExternalId) = 1;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    INSERT INTO "ExternalIdentities" ("Id", "UserId", "Authority", "Subject", "CreatedAt", "LastSeenAt")
                    SELECT u."Id", u."Id", 'urn:nodepilot:identity:active-directory', u."ExternalId", u."CreatedAt", CURRENT_TIMESTAMP
                    FROM "Users" u
                    WHERE u."Provider" = 'Windows' AND u."ExternalId" IS NOT NULL
                      AND (SELECT COUNT(*) FROM "Users" d WHERE d."Provider" = 'Windows' AND d."ExternalId" = u."ExternalId") = 1;

                    INSERT INTO "ExternalIdentities" ("Id", "UserId", "Authority", "Subject", "CreatedAt", "LastSeenAt")
                    SELECT u."Id", u."Id", 'urn:nodepilot:identity:legacy-ldap-object-guid', u."ExternalId", u."CreatedAt", CURRENT_TIMESTAMP
                    FROM "Users" u
                    WHERE u."Provider" = 'Ldap' AND u."ExternalId" IS NOT NULL
                      AND (SELECT COUNT(*) FROM "Users" d WHERE d."Provider" = 'Ldap' AND d."ExternalId" = u."ExternalId") = 1;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    INSERT INTO "ExternalIdentities" ("Id", "UserId", "Authority", "Subject", "CreatedAt", "LastSeenAt")
                    SELECT u."Id", u."Id", 'urn:nodepilot:identity:active-directory', u."ExternalId", u."CreatedAt", CURRENT_TIMESTAMP
                    FROM "Users" u
                    WHERE u."Provider" = 'Windows' AND u."ExternalId" IS NOT NULL
                      AND (SELECT COUNT(*) FROM "Users" d WHERE d."Provider" = 'Windows' AND d."ExternalId" = u."ExternalId") = 1;

                    INSERT INTO "ExternalIdentities" ("Id", "UserId", "Authority", "Subject", "CreatedAt", "LastSeenAt")
                    SELECT u."Id", u."Id", 'urn:nodepilot:identity:legacy-ldap-object-guid', u."ExternalId", u."CreatedAt", CURRENT_TIMESTAMP
                    FROM "Users" u
                    WHERE u."Provider" = 'Ldap' AND u."ExternalId" IS NOT NULL
                      AND (SELECT COUNT(*) FROM "Users" d WHERE d."Provider" = 'Ldap' AND d."ExternalId" = u."ExternalId") = 1;
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ExpiresAt",
                table: "AuthSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId",
                table: "AuthSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_GroupKey",
                table: "DirectoryMemberships",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Authority_Subject",
                table: "ExternalIdentities",
                columns: new[] { "Authority", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserId",
                table: "ExternalIdentities",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "DirectoryMemberships");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropColumn(
                name: "DirectorySyncStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsBreakGlass",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsTombstoned",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastDirectorySyncAt",
                table: "Users");
        }
    }
}
