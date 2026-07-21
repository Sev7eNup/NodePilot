using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <summary>
    /// Removes the two orphaned personal-folders tables (<c>UserWorkflowFolders</c>,
    /// <c>WorkflowFolderAssignments</c>) that <c>InitialBaseline</c> still creates but nothing
    /// maps or references. They were meant to be dropped by
    /// <c>20260512000000_RemoveUserWorkflowFolders</c>, which was deliberately left invisible to
    /// EF because a plain <c>DropTable</c> (no IF EXISTS in the provider-agnostic builder) would
    /// crash on databases where the tables were already removed out-of-band.
    /// </summary>
    /// <remarks>
    /// Hand-authored (no Designer file): the model is unchanged (both tables are unmapped, so the
    /// snapshot already omits them), but EF's migration discovery filters on the
    /// <see cref="DbContextAttribute"/> — without it <c>Migrate()</c> silently skips the class.
    /// This is the "new attributed migration" the invisible migration's doc comment pointed to.
    /// The drops are idempotent (<c>DROP TABLE IF EXISTS</c>) so this applies cleanly whether or
    /// not the tables still exist. Neither table has any foreign keys, so drop order is free.
    /// </remarks>
    [DbContext(typeof(NodePilotDbContext))]
    [Migration("20260714094500_DropOrphanedUserFolderTables")]
    public partial class DropOrphanedUserFolderTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
            // SQL Server quotes identifiers with brackets; Npgsql and SQLite both use double quotes.
            foreach (var table in new[] { "WorkflowFolderAssignments", "UserWorkflowFolders" })
            {
                var quoted = isSqlServer ? $"[{table}]" : $"\"{table}\"";
                migrationBuilder.Sql($"DROP TABLE IF EXISTS {quoted};");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate the (still unmapped) tables so the migration is reversible, matching the
            // original InitialBaseline shape. Store types come from the active provider via the
            // MigrationModelPortability convention, exactly as in InitialBaseline.
            migrationBuilder.CreateTable(
                name: "UserWorkflowFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    SortOrder = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkflowFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowFolderAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    FolderId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowFolderAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkflowFolders_UserId_ParentFolderId",
                table: "UserWorkflowFolders",
                columns: new[] { "UserId", "ParentFolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowFolderAssignments_FolderId",
                table: "WorkflowFolderAssignments",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowFolderAssignments_UserId_WorkflowId",
                table: "WorkflowFolderAssignments",
                columns: new[] { "UserId", "WorkflowId" },
                unique: true);
        }
    }
}
