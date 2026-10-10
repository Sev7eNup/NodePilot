using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceLiveCustomActivityKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomActivityDefinitions_Key",
                table: "CustomActivityDefinitions");

            migrationBuilder.CreateIndex(
                name: "UX_CustomActivityDefinitions_LiveKey",
                table: "CustomActivityDefinitions",
                column: "Key",
                unique: true,
                filter: CustomActivityKeyConstraint.Filter(ActiveProvider));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CustomActivityDefinitions_LiveKey",
                table: "CustomActivityDefinitions");

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitions_Key",
                table: "CustomActivityDefinitions",
                column: "Key");
        }
    }
}
