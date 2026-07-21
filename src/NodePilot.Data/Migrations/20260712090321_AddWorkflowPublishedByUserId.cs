using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowPublishedByUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublishedByUserId",
                table: "Workflows",
                nullable: true);

            // Preserve the pre-migration trigger authority once, then keep it stable:
            // old runtime resolution used UpdatedBy and fell back to CreatedBy. Rows
            // without a resolvable user remain null and fail closed until re-published.
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.Sql("""
                    UPDATE w
                    SET PublishedByUserId = COALESCE(
                        (SELECT TOP (1) u.Id FROM Users u WHERE u.Username = w.UpdatedBy),
                        (SELECT TOP (1) u.Id FROM Users u WHERE u.Username = w.CreatedBy))
                    FROM Workflows w;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    UPDATE "Workflows" AS w
                    SET "PublishedByUserId" = COALESCE(
                        (SELECT u."Id" FROM "Users" AS u WHERE u."Username" = w."UpdatedBy" LIMIT 1),
                        (SELECT u."Id" FROM "Users" AS u WHERE u."Username" = w."CreatedBy" LIMIT 1));
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    UPDATE "Workflows"
                    SET "PublishedByUserId" = COALESCE(
                        (SELECT u."Id" FROM "Users" AS u WHERE u."Username" = "Workflows"."UpdatedBy" LIMIT 1),
                        (SELECT u."Id" FROM "Users" AS u WHERE u."Username" = "Workflows"."CreatedBy" LIMIT 1));
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedByUserId",
                table: "Workflows");
        }
    }
}
