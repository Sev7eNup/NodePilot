using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowExecutionCompletedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_CompletedAt_Id",
                table: "WorkflowExecutions",
                columns: new[] { "CompletedAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "Status" })
                .Annotation("Npgsql:IndexInclude", new[] { "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_CompletedAt_Id",
                table: "WorkflowExecutions");
        }
    }
}
