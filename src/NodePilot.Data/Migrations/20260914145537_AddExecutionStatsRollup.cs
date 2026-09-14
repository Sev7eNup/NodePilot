using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionStatsRollup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionHourlyStats",
                columns: table => new
                {
                    HourUtc = table.Column<DateTime>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TotalCount = table.Column<int>(nullable: false),
                    SucceededCount = table.Column<int>(nullable: false),
                    FailedCount = table.Column<int>(nullable: false),
                    CancelledCount = table.Column<int>(nullable: false),
                    RunningCount = table.Column<int>(nullable: false),
                    RetriedCount = table.Column<int>(nullable: false),
                    FinishedCount = table.Column<int>(nullable: false),
                    DurationMsSum = table.Column<long>(nullable: false),
                    DurationMsCount = table.Column<int>(nullable: false),
                    IsFinal = table.Column<bool>(nullable: false),
                    ComputedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionHourlyStats", x => new { x.HourUtc, x.WorkflowId });
                    table.ForeignKey(
                        name: "FK_ExecutionHourlyStats_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionStatsRollupStates",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false),
                    CoverageStartUtc = table.Column<DateTime>(nullable: true),
                    CoverageEndUtc = table.Column<DateTime>(nullable: true),
                    BackfillComplete = table.Column<bool>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionStatsRollupStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FailureCauseHourlyStats",
                columns: table => new
                {
                    HourUtc = table.Column<DateTime>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    MessageHash = table.Column<string>(maxLength: 64, nullable: false),
                    Message = table.Column<string>(nullable: true),
                    Count = table.Column<int>(nullable: false),
                    LatestExecutionId = table.Column<Guid>(nullable: false),
                    LatestStartedAt = table.Column<DateTime>(nullable: false),
                    IsFinal = table.Column<bool>(nullable: false),
                    ComputedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureCauseHourlyStats", x => new { x.HourUtc, x.WorkflowId, x.MessageHash });
                    table.ForeignKey(
                        name: "FK_FailureCauseHourlyStats_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionHourlyStats_IsFinal_HourUtc",
                table: "ExecutionHourlyStats",
                columns: new[] { "IsFinal", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionHourlyStats_WorkflowId",
                table: "ExecutionHourlyStats",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_FailureCauseHourlyStats_IsFinal_HourUtc",
                table: "FailureCauseHourlyStats",
                columns: new[] { "IsFinal", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureCauseHourlyStats_WorkflowId",
                table: "FailureCauseHourlyStats",
                column: "WorkflowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionHourlyStats");

            migrationBuilder.DropTable(
                name: "ExecutionStatsRollupStates");

            migrationBuilder.DropTable(
                name: "FailureCauseHourlyStats");
        }
    }
}
