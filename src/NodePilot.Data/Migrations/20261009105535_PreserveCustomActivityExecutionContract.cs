using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreserveCustomActivityExecutionContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ActivityType",
                table: "SupportEvents",
                maxLength: 71,
                nullable: true,
                oldClrType: typeof(string),
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "StepType",
                table: "StepExecutions",
                maxLength: 71,
                nullable: false,
                oldClrType: typeof(string),
                oldMaxLength: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ActivityType",
                table: "SupportEvents",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldMaxLength: 71,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "StepType",
                table: "StepExecutions",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldMaxLength: 71);
        }
    }
}
