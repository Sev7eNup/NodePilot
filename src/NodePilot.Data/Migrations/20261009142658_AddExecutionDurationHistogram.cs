using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionDurationHistogram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DurationHistogram",
                table: "ExecutionHourlyStats",
                nullable: false,
                defaultValue: "");

            // Existing buckets have no histogram yet. Without its state row the rollup rebuilds every
            // bucket within its retention; until then the dashboard reads raw rows.
            migrationBuilder.Sql("DELETE FROM \"ExecutionStatsRollupStates\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationHistogram",
                table: "ExecutionHourlyStats");
        }
    }
}
