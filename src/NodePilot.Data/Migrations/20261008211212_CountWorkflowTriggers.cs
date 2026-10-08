using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class CountWorkflowTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The startup backfill recomputes counts with triggers included.
            migrationBuilder.Sql("UPDATE \"Workflows\" SET \"TriggerTypesJson\" = NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Workflows\" SET \"TriggerTypesJson\" = NULL");
        }
    }
}
