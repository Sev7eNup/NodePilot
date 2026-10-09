using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetriedStepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_Retried",
                table: "StepExecutions",
                column: "WorkflowExecutionId",
                filter: "\"AttemptCount\" > 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StepExecutions_Retried",
                table: "StepExecutions");
        }
    }
}
