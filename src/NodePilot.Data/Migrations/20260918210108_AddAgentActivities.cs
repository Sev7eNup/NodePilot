using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentMcpServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(nullable: false),
                    Transport = table.Column<string>(maxLength: 20, nullable: false),
                    Command = table.Column<string>(maxLength: 1024, nullable: true),
                    ArgumentsJson = table.Column<string>(nullable: false),
                    Endpoint = table.Column<string>(maxLength: 2048, nullable: true),
                    ProtectedSecrets = table.Column<byte[]>(nullable: true),
                    SecretProvider = table.Column<string>(maxLength: 40, nullable: true),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMcpServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowExecutionId = table.Column<Guid>(nullable: false),
                    StepId = table.Column<string>(maxLength: 256, nullable: false),
                    Status = table.Column<string>(maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(nullable: false),
                    CompletedAt = table.Column<DateTime>(nullable: true),
                    Result = table.Column<string>(nullable: true),
                    Error = table.Column<string>(nullable: true),
                    ModelCalls = table.Column<int>(nullable: false),
                    ToolCalls = table.Column<int>(nullable: false),
                    Delegations = table.Column<int>(nullable: false),
                    InputTokens = table.Column<long>(nullable: true),
                    OutputTokens = table.Column<long>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRuns_WorkflowExecutions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentSkillPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 128, nullable: false),
                    Version = table.Column<string>(maxLength: 64, nullable: false),
                    Description = table.Column<string>(maxLength: 1024, nullable: false),
                    Sha256 = table.Column<string>(maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(nullable: false),
                    Package = table.Column<byte[]>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkillPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRunEvents",
                columns: table => new
                {
                    AgentRunId = table.Column<Guid>(nullable: false),
                    Sequence = table.Column<long>(nullable: false),
                    Timestamp = table.Column<DateTime>(nullable: false),
                    MemberId = table.Column<string>(maxLength: 64, nullable: true),
                    Kind = table.Column<string>(maxLength: 40, nullable: false),
                    ToolName = table.Column<string>(maxLength: 128, nullable: true),
                    Content = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRunEvents", x => new { x.AgentRunId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_AgentRunEvents_AgentRuns_AgentRunId",
                        column: x => x.AgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkflowExecutionId_StepId",
                table: "AgentRuns",
                columns: new[] { "WorkflowExecutionId", "StepId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkillPackages_Name_Version",
                table: "AgentSkillPackages",
                columns: new[] { "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentMcpServers");

            migrationBuilder.DropTable(
                name: "AgentRunEvents");

            migrationBuilder.DropTable(
                name: "AgentSkillPackages");

            migrationBuilder.DropTable(
                name: "AgentRuns");
        }
    }
}
