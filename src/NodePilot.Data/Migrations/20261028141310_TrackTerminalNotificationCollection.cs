using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackTerminalNotificationCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_CompletedAt_Id",
                table: "WorkflowExecutions");

            migrationBuilder.AddColumn<bool>(
                name: "TerminalNotificationCollected",
                table: "WorkflowExecutions",
                nullable: false,
                defaultValue: false);

            var collected = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL" ? "TRUE" : "1";
            migrationBuilder.Sql($"""
                UPDATE "WorkflowExecutions" SET "TerminalNotificationCollected" = {collected}
                WHERE "Status" IN ('Succeeded', 'Failed', 'Cancelled') AND "CompletedAt" IS NOT NULL
                  AND EXISTS (
                    SELECT 1 FROM "NotificationDispatcherStates" s
                    WHERE s."Id" = '00000000-0000-0000-0000-0000000000A1'
                      AND ("WorkflowExecutions"."CompletedAt" < s."LastCompletedAtSeen"
                        OR ("WorkflowExecutions"."CompletedAt" = s."LastCompletedAtSeen"
                          AND "WorkflowExecutions"."Id" <= COALESCE(s."LastIdSeen", '00000000-0000-0000-0000-000000000000'))))
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "InitializedAt",
                table: "NotificationDispatcherStates",
                nullable: false,
                defaultValue: DateTime.UnixEpoch);

            migrationBuilder.Sql("""
                UPDATE "NotificationDispatcherStates"
                SET "InitializedAt" = COALESCE("LastCompletedAtSeen", "UpdatedAt")
                """);
            migrationBuilder.DropColumn(name: "LastCompletedAtSeen", table: "NotificationDispatcherStates");
            migrationBuilder.DropColumn(name: "LastIdSeen", table: "NotificationDispatcherStates");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_NotificationCollection",
                table: "WorkflowExecutions",
                columns: new[] { "TerminalNotificationCollected", "CompletedAt", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "Status" })
                .Annotation("SqlServer:Include", new[] { "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_NotificationCollection",
                table: "WorkflowExecutions");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCompletedAtSeen",
                table: "NotificationDispatcherStates",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastIdSeen",
                table: "NotificationDispatcherStates",
                nullable: true);

            var collected = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL" ? "TRUE" : "1";
            var first = ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer" ? "TOP (1) " : "";
            var limit = ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer" ? "" : " LIMIT 1";
            var latest = $"FROM \"WorkflowExecutions\" WHERE \"TerminalNotificationCollected\" = {collected} "
                + $"AND \"CompletedAt\" IS NOT NULL ORDER BY \"CompletedAt\" DESC, \"Id\" DESC{limit}";
            migrationBuilder.Sql($"""
                UPDATE "NotificationDispatcherStates"
                SET "LastCompletedAtSeen" = COALESCE((SELECT {first}"CompletedAt" {latest}), "InitializedAt"),
                    "LastIdSeen" = (SELECT {first}"Id" {latest})
                """);
            migrationBuilder.DropColumn(name: "TerminalNotificationCollected", table: "WorkflowExecutions");
            migrationBuilder.DropColumn(name: "InitializedAt", table: "NotificationDispatcherStates");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_CompletedAt_Id",
                table: "WorkflowExecutions",
                columns: new[] { "CompletedAt", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "Status" })
                .Annotation("SqlServer:Include", new[] { "Status" });
        }
    }
}
