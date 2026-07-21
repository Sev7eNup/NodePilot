using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeDirectoryMembershipsAndSessionRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var storeTypes = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => new
                {
                    Guid = "uniqueidentifier", String64 = "nvarchar(64)",
                    String384 = "nvarchar(384)", Integer = "int",
                },
                "Npgsql.EntityFrameworkCore.PostgreSQL" => new
                {
                    Guid = "uuid", String64 = "character varying(64)",
                    String384 = "character varying(384)", Integer = "integer",
                },
                "Microsoft.EntityFrameworkCore.Sqlite" => new
                {
                    Guid = "TEXT", String64 = "TEXT", String384 = "TEXT", Integer = "INTEGER",
                },
                _ => throw new System.NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };
            var membershipIdDefault = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => "NEWID()",
                "Npgsql.EntityFrameworkCore.PostgreSQL" => "gen_random_uuid()",
                "Microsoft.EntityFrameworkCore.Sqlite" =>
                    "(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))))",
                _ => throw new System.NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };

            migrationBuilder.DropPrimaryKey(
                name: "PK_DirectoryMemberships",
                table: "DirectoryMemberships");

            migrationBuilder.DropIndex(
                name: "IX_DirectoryMemberships_GroupKey",
                table: "DirectoryMemberships");

            migrationBuilder.AddColumn<string>(
                name: "Authority",
                table: "DirectoryMemberships",
                type: storeTypes.String384,
                maxLength: 384,
                nullable: false,
                defaultValue: "urn:nodepilot:identity:active-directory");

            migrationBuilder.AddColumn<System.Guid>(
                name: "Id",
                table: "DirectoryMemberships",
                type: storeTypes.Guid,
                nullable: false,
                defaultValueSql: membershipIdDefault);

            migrationBuilder.AddColumn<string>(
                name: "CurrentJti",
                table: "AuthSessions",
                type: storeTypes.String64,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RefreshGeneration",
                table: "AuthSessions",
                type: storeTypes.Integer,
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DirectoryMemberships",
                table: "DirectoryMemberships",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_Authority_GroupKey",
                table: "DirectoryMemberships",
                columns: new[] { "Authority", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_UserId_Authority_GroupKey",
                table: "DirectoryMemberships",
                columns: new[] { "UserId", "Authority", "GroupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older releases understand DirectoryMemberships as AD-only. Remove OIDC/SCIM
            // rows before dropping Authority so a downgrade cannot reinterpret them as AD
            // grants or fail the restored (UserId, GroupKey) primary key. Downgrading is
            // therefore intentionally destructive for non-AD authorization snapshots.
            var deleteNonAdMemberships = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" =>
                    "DELETE FROM [DirectoryMemberships] WHERE [Authority] <> 'urn:nodepilot:identity:active-directory';",
                "Npgsql.EntityFrameworkCore.PostgreSQL" or "Microsoft.EntityFrameworkCore.Sqlite" =>
                    "DELETE FROM \"DirectoryMemberships\" WHERE \"Authority\" <> 'urn:nodepilot:identity:active-directory';",
                _ => throw new System.NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };
            migrationBuilder.Sql(deleteNonAdMemberships);

            migrationBuilder.DropPrimaryKey(
                name: "PK_DirectoryMemberships",
                table: "DirectoryMemberships");

            migrationBuilder.DropIndex(
                name: "IX_DirectoryMemberships_Authority_GroupKey",
                table: "DirectoryMemberships");

            migrationBuilder.DropIndex(
                name: "IX_DirectoryMemberships_UserId_Authority_GroupKey",
                table: "DirectoryMemberships");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "DirectoryMemberships");

            migrationBuilder.DropColumn(
                name: "Authority",
                table: "DirectoryMemberships");

            migrationBuilder.DropColumn(
                name: "CurrentJti",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "RefreshGeneration",
                table: "AuthSessions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DirectoryMemberships",
                table: "DirectoryMemberships",
                columns: new[] { "UserId", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_GroupKey",
                table: "DirectoryMemberships",
                column: "GroupKey");
        }
    }
}
