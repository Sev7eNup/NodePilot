using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionDispatchOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionDispatchOutbox",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggeredBy = table.Column<string>(maxLength: 100, nullable: false),
                    ProtectedParameters = table.Column<byte[]>(nullable: true),
                    TimeoutSeconds = table.Column<int>(nullable: true),
                    DebugEnabled = table.Column<bool>(nullable: false),
                    StartedByUserId = table.Column<Guid>(nullable: true),
                    ParentExecutionId = table.Column<Guid>(nullable: true),
                    CallDepth = table.Column<int>(nullable: false),
                    RequireWorkflowEnabled = table.Column<bool>(nullable: false),
                    MissingWorkflowMessage = table.Column<string>(maxLength: 2000, nullable: false),
                    PreOwnershipFailurePrefix = table.Column<string>(maxLength: 1000, nullable: false),
                    Priority = table.Column<int>(nullable: false),
                    RequireMaintenanceWindowCheck = table.Column<bool>(nullable: false),
                    BypassMaintenanceWindow = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    AvailableAt = table.Column<DateTime>(nullable: false),
                    LeaseOwner = table.Column<string>(maxLength: 240, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(nullable: true),
                    AttemptCount = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionDispatchOutbox", x => x.ExecutionId);
                    table.ForeignKey(
                        name: "FK_ExecutionDispatchOutbox_WorkflowExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionDispatchOutbox_AvailableAt_Priority",
                table: "ExecutionDispatchOutbox",
                columns: new[] { "AvailableAt", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionDispatchOutbox_LeaseExpiresAt",
                table: "ExecutionDispatchOutbox",
                column: "LeaseExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionDispatchOutbox");
        }
    }
}
