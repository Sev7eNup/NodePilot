using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackNotificationOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationOccurrenceStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    EventKey = table.Column<string>(maxLength: 300, nullable: false),
                    LastObservedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOccurrenceStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOccurrenceStates_LastObservedAt",
                table: "NotificationOccurrenceStates",
                column: "LastObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOccurrenceStates_NotificationRuleId_EventKey",
                table: "NotificationOccurrenceStates",
                columns: new[] { "NotificationRuleId", "EventKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationOccurrenceStates");
        }
    }
}
