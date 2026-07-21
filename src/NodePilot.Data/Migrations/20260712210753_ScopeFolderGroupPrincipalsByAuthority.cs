using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeFolderGroupPrincipalsByAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var string512 = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => "nvarchar(512)",
                "Npgsql.EntityFrameworkCore.PostgreSQL" => "character varying(512)",
                "Microsoft.EntityFrameworkCore.Sqlite" => "TEXT",
                _ => throw new NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };
            migrationBuilder.DropIndex(
                name: "IX_SharedFolderPermissions_FolderId_PrincipalType_PrincipalKey",
                table: "SharedFolderPermissions");

            migrationBuilder.AddColumn<string>(
                name: "PrincipalAuthority",
                table: "SharedFolderPermissions",
                type: string512,
                maxLength: 512,
                collation: migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                    ? "Latin1_General_100_BIN2"
                    : null,
                nullable: false,
                defaultValue: "");

            var adAuthority = "urn:nodepilot:identity:active-directory";
            migrationBuilder.Sql(migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" =>
                    $"UPDATE [SharedFolderPermissions] SET [PrincipalAuthority] = N'{adAuthority}' WHERE [PrincipalType] = N'Group' AND [PrincipalAuthority] = N'';",
                "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                    $"UPDATE \"SharedFolderPermissions\" SET \"PrincipalAuthority\" = '{adAuthority}' WHERE \"PrincipalType\" = 'Group' AND \"PrincipalAuthority\" = '';",
                "Microsoft.EntityFrameworkCore.Sqlite" =>
                    $"UPDATE \"SharedFolderPermissions\" SET \"PrincipalAuthority\" = '{adAuthority}' WHERE \"PrincipalType\" = 'Group' AND \"PrincipalAuthority\" = '';",
                _ => throw new NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            });

            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql(
                    "ALTER TABLE [SharedFolderPermissions] ALTER COLUMN [PrincipalKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL;");
            }

            migrationBuilder.CreateIndex(
                name: "UX_SharedFolderPermissions_Principal",
                table: "SharedFolderPermissions",
                columns: new[] { "FolderId", "PrincipalType", "PrincipalAuthority", "PrincipalKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql("""
                    IF EXISTS (
                        SELECT 1
                        FROM [SharedFolderPermissions]
                        GROUP BY [FolderId], [PrincipalType], [PrincipalKey]
                        HAVING COUNT(*) > 1
                    )
                        THROW 51000, 'Cannot remove group-principal authorities: authority-distinct folder grants would collide.', 1;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1
                            FROM "SharedFolderPermissions"
                            GROUP BY "FolderId", "PrincipalType", "PrincipalKey"
                            HAVING COUNT(*) > 1
                        ) THEN
                            RAISE EXCEPTION 'Cannot remove group-principal authorities: authority-distinct folder grants would collide.';
                        END IF;
                    END $$;
                    """);
            }

            migrationBuilder.DropIndex(
                name: "UX_SharedFolderPermissions_Principal",
                table: "SharedFolderPermissions");

            migrationBuilder.DropColumn(
                name: "PrincipalAuthority",
                table: "SharedFolderPermissions");

            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql(
                    "ALTER TABLE [SharedFolderPermissions] ALTER COLUMN [PrincipalKey] nvarchar(256) COLLATE DATABASE_DEFAULT NOT NULL;");
            }

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderPermissions_FolderId_PrincipalType_PrincipalKey",
                table: "SharedFolderPermissions",
                columns: new[] { "FolderId", "PrincipalType", "PrincipalKey" },
                unique: true);
        }
    }
}
