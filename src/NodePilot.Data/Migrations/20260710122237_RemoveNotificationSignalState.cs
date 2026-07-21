using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNotificationSignalState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationSignalStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationSignalStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    LastChangedAt = table.Column<DateTime>(nullable: false),
                    LastState = table.Column<string>(maxLength: 50, nullable: false),
                    SourceKey = table.Column<string>(maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSignalStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSignalStates_SourceKey",
                table: "NotificationSignalStates",
                column: "SourceKey",
                unique: true);
        }
    }
}
