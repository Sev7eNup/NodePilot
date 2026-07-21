using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomActivityDefinitions : Migration
    {
        // NOTE: provider-agnostic — all `type:` annotations stripped per the repo convention so the
        // single migration set applies to both PostgreSQL and SQL Server.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomActivityHash",
                table: "StepExecutions",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomActivityKey",
                table: "StepExecutions",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomActivityVersion",
                table: "StepExecutions",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomActivityDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Key = table.Column<string>(maxLength: 64, nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    Icon = table.Column<string>(maxLength: 60, nullable: false),
                    Color = table.Column<string>(maxLength: 32, nullable: true),
                    ScriptTemplate = table.Column<string>(nullable: false),
                    Engine = table.Column<string>(maxLength: 20, nullable: false),
                    RunsRemote = table.Column<bool>(nullable: false),
                    Isolated = table.Column<bool>(nullable: false),
                    MemoryLimitMb = table.Column<int>(nullable: true),
                    MaxProcesses = table.Column<int>(nullable: true),
                    DefaultTimeoutSeconds = table.Column<int>(nullable: true),
                    SuccessExitCodes = table.Column<string>(maxLength: 100, nullable: true),
                    InputParametersJson = table.Column<string>(nullable: false),
                    OutputParametersJson = table.Column<string>(nullable: false),
                    IsEnabled = table.Column<bool>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    IsDeleted = table.Column<bool>(nullable: false),
                    DeletedAt = table.Column<DateTime>(nullable: true),
                    ConcurrencyToken = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    ChangeNote = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomActivityDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomActivityDefinitionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    DefinitionId = table.Column<Guid>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    Icon = table.Column<string>(maxLength: 60, nullable: false),
                    Color = table.Column<string>(maxLength: 32, nullable: true),
                    ScriptTemplate = table.Column<string>(nullable: false),
                    Engine = table.Column<string>(maxLength: 20, nullable: false),
                    RunsRemote = table.Column<bool>(nullable: false),
                    Isolated = table.Column<bool>(nullable: false),
                    MemoryLimitMb = table.Column<int>(nullable: true),
                    MaxProcesses = table.Column<int>(nullable: true),
                    DefaultTimeoutSeconds = table.Column<int>(nullable: true),
                    SuccessExitCodes = table.Column<string>(maxLength: 100, nullable: true),
                    InputParametersJson = table.Column<string>(nullable: false),
                    OutputParametersJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    ChangeNote = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomActivityDefinitionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomActivityDefinitionVersions_CustomActivityDefinitions_~",
                        column: x => x.DefinitionId,
                        principalTable: "CustomActivityDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitions_IsDeleted_IsEnabled",
                table: "CustomActivityDefinitions",
                columns: new[] { "IsDeleted", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitions_Key",
                table: "CustomActivityDefinitions",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitionVersions_DefinitionId_Version",
                table: "CustomActivityDefinitionVersions",
                columns: new[] { "DefinitionId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomActivityDefinitionVersions");

            migrationBuilder.DropTable(
                name: "CustomActivityDefinitions");

            migrationBuilder.DropColumn(
                name: "CustomActivityHash",
                table: "StepExecutions");

            migrationBuilder.DropColumn(
                name: "CustomActivityKey",
                table: "StepExecutions");

            migrationBuilder.DropColumn(
                name: "CustomActivityVersion",
                table: "StepExecutions");
        }
    }
}
