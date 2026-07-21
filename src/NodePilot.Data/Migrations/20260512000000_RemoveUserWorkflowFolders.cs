using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <summary>
    /// INTENTIONALLY INVISIBLE TO EF — do not "fix" by adding attributes.
    ///
    /// This hand-authored migration deliberately carries neither <c>[DbContext]</c> nor
    /// <c>[Migration]</c>, so EF's migration discovery skips it and <c>Migrate()</c> never
    /// applies it (the discovery rule is documented in the sibling
    /// <c>20260702120000_AddNotificationRouteConditionExpression</c>, where the missing
    /// attribute once caused a real schema-drift crash).
    ///
    /// Why it stays invisible: it was meant to drop the personal-folders tables
    /// (<c>UserWorkflowFolders</c>, <c>WorkflowFolderAssignments</c>) after the feature was
    /// removed in May 2026, but real databases diverged — on some, the tables were already
    /// dropped out-of-band. Making this migration discoverable now would enqueue it on every
    /// database whose <c>__EFMigrationsHistory</c> lacks the entry and crash where the tables
    /// no longer exist (<c>DropTable</c> has no IF-EXISTS in the provider-agnostic builder).
    ///
    /// Consequence: fresh databases get both tables from <c>InitialBaseline</c> and keep them
    /// as empty orphans — harmless, nothing in the codebase references them. If cleanup is
    /// ever wanted, write a NEW attributed migration with idempotent, provider-aware drops;
    /// do not resurrect this one.
    /// </summary>
    public partial class RemoveUserWorkflowFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowFolderAssignments_UserId_WorkflowId",
                table: "WorkflowFolderAssignments");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowFolderAssignments_FolderId",
                table: "WorkflowFolderAssignments");

            migrationBuilder.DropIndex(
                name: "IX_UserWorkflowFolders_UserId_ParentFolderId",
                table: "UserWorkflowFolders");

            migrationBuilder.DropTable(
                name: "WorkflowFolderAssignments");

            migrationBuilder.DropTable(
                name: "UserWorkflowFolders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserWorkflowFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    SortOrder = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
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
                    FolderId = table.Column<Guid>(nullable: false),
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
