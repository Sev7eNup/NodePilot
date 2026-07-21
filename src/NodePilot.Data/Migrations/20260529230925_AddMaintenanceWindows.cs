using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 100, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(nullable: false),
                    Mode = table.Column<string>(maxLength: 20, nullable: false),
                    ScopeKind = table.Column<string>(maxLength: 20, nullable: false),
                    Recurrence = table.Column<string>(maxLength: 20, nullable: false),
                    OneTimeStartUtc = table.Column<DateTime>(nullable: true),
                    OneTimeEndUtc = table.Column<DateTime>(nullable: true),
                    WeeklyDaysMask = table.Column<int>(nullable: false),
                    WeeklyStartMinuteOfDay = table.Column<int>(nullable: true),
                    WeeklyEndMinuteOfDay = table.Column<int>(nullable: true),
                    CronExpression = table.Column<string>(maxLength: 120, nullable: true),
                    DurationMinutes = table.Column<int>(nullable: true),
                    TimeZoneId = table.Column<string>(maxLength: 100, nullable: false),
                    DeferralPolicy = table.Column<string>(maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindowTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    MaintenanceWindowId = table.Column<Guid>(nullable: false),
                    TargetKind = table.Column<string>(maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindowTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowTargets_MaintenanceWindows_MaintenanceWind~",
                        column: x => x.MaintenanceWindowId,
                        principalTable: "MaintenanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_IsEnabled",
                table: "MaintenanceWindows",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_Name",
                table: "MaintenanceWindows",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowTargets_MaintenanceWindowId_TargetKind_Tar~",
                table: "MaintenanceWindowTargets",
                columns: new[] { "MaintenanceWindowId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowTargets_TargetId",
                table: "MaintenanceWindowTargets",
                column: "TargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceWindowTargets");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");
        }
    }
}
