using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemAlertPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivatedAt",
                table: "NotificationRules",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "NotificationRules",
                maxLength: 20,
                nullable: false,
                defaultValue: "Custom");

            migrationBuilder.AddColumn<string>(
                name: "SeverityOverride",
                table: "NotificationRules",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceParametersJson",
                table: "NotificationRules",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SustainForSeconds",
                table: "NotificationRules",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SystemPresetId",
                table: "NotificationRules",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemSourceId",
                table: "NotificationRules",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SystemAlertPolicyStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    SourceId = table.Column<string>(maxLength: 100, nullable: false),
                    InstanceKey = table.Column<string>(maxLength: 300, nullable: false),
                    IsMatching = table.Column<bool>(nullable: false),
                    MatchStartedAt = table.Column<DateTime>(nullable: true),
                    EpisodeStartedAt = table.Column<DateTime>(nullable: true),
                    LastObservedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemAlertPolicyStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemAlertSourceStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    SourceId = table.Column<string>(maxLength: 100, nullable: false),
                    StateKey = table.Column<string>(maxLength: 200, nullable: false),
                    CursorJson = table.Column<string>(nullable: true),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemAlertSourceStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_Kind_IsEnabled",
                table: "NotificationRules",
                columns: new[] { "Kind", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertPolicyStates_LastObservedAt",
                table: "SystemAlertPolicyStates",
                column: "LastObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertPolicyStates_NotificationRuleId_SourceId_Instanc~",
                table: "SystemAlertPolicyStates",
                columns: new[] { "NotificationRuleId", "SourceId", "InstanceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertSourceStates_SourceId_StateKey",
                table: "SystemAlertSourceStates",
                columns: new[] { "SourceId", "StateKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemAlertPolicyStates");

            migrationBuilder.DropTable(
                name: "SystemAlertSourceStates");

            migrationBuilder.DropIndex(
                name: "IX_NotificationRules_Kind_IsEnabled",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "ActivatedAt",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "SeverityOverride",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "SourceParametersJson",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "SustainForSeconds",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "SystemPresetId",
                table: "NotificationRules");

            migrationBuilder.DropColumn(
                name: "SystemSourceId",
                table: "NotificationRules");
        }
    }
}
