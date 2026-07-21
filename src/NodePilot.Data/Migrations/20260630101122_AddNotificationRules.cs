using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    NotificationRouteId = table.Column<Guid>(nullable: false),
                    EventKey = table.Column<string>(maxLength: 300, nullable: false),
                    DedupKey = table.Column<string>(maxLength: 300, nullable: false),
                    Status = table.Column<string>(maxLength: 20, nullable: false),
                    Attempt = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    SentAt = table.Column<DateTime>(nullable: true),
                    Error = table.Column<string>(maxLength: 2000, nullable: true),
                    IsTest = table.Column<bool>(nullable: false),
                    Summary = table.Column<string>(maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveryAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatcherStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    LastCompletedAtSeen = table.Column<DateTime>(nullable: true),
                    LastIdSeen = table.Column<Guid>(nullable: true),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatcherStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 100, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(nullable: false),
                    EventTypes = table.Column<string>(maxLength: 200, nullable: false),
                    FilterExpressionJson = table.Column<string>(nullable: true),
                    ScopeKind = table.Column<string>(maxLength: 20, nullable: false),
                    CooldownMinutes = table.Column<int>(nullable: false),
                    DedupKeyTemplate = table.Column<string>(maxLength: 300, nullable: true),
                    MinOccurrences = table.Column<int>(nullable: false),
                    OccurrenceWindowMinutes = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSignalStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    SourceKey = table.Column<string>(maxLength: 200, nullable: false),
                    LastState = table.Column<string>(maxLength: 50, nullable: false),
                    LastChangedAt = table.Column<DateTime>(nullable: false),
                    LastAlertedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSignalStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSuppressionStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    DedupKey = table.Column<string>(maxLength: 300, nullable: false),
                    LastFiredAt = table.Column<DateTime>(nullable: true),
                    OccurrenceCount = table.Column<int>(nullable: false),
                    WindowStartedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSuppressionStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    Channel = table.Column<string>(maxLength: 20, nullable: false),
                    Target = table.Column<string>(maxLength: 1000, nullable: false),
                    Secret = table.Column<string>(maxLength: 2000, nullable: true),
                    Order = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRoutes_NotificationRules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "NotificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRuleTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    TargetKind = table.Column<string>(maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRuleTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRuleTargets_NotificationRules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "NotificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_CreatedAt",
                table: "NotificationDeliveryAttempts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_NotificationRuleId_Notificatio~",
                table: "NotificationDeliveryAttempts",
                columns: new[] { "NotificationRuleId", "NotificationRouteId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoutes_NotificationRuleId",
                table: "NotificationRoutes",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_IsEnabled",
                table: "NotificationRules",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_Name",
                table: "NotificationRules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleTargets_NotificationRuleId_TargetKind_Targe~",
                table: "NotificationRuleTargets",
                columns: new[] { "NotificationRuleId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleTargets_TargetId",
                table: "NotificationRuleTargets",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSignalStates_SourceKey",
                table: "NotificationSignalStates",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSuppressionStates_NotificationRuleId_DedupKey",
                table: "NotificationSuppressionStates",
                columns: new[] { "NotificationRuleId", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "NotificationDispatcherStates");

            migrationBuilder.DropTable(
                name: "NotificationRoutes");

            migrationBuilder.DropTable(
                name: "NotificationRuleTargets");

            migrationBuilder.DropTable(
                name: "NotificationSignalStates");

            migrationBuilder.DropTable(
                name: "NotificationSuppressionStates");

            migrationBuilder.DropTable(
                name: "NotificationRules");
        }
    }
}
