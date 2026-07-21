using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provider-agnostisch: keine `type:`-Strings — EF Core leitet den Spaltentyp aus
            // dem CLR-Property + Provider-Default-Mapping (Guid → uuid auf Postgres /
            // uniqueidentifier auf SqlServer, DateTime → timestamp with time zone /
            // datetime2, string mit MaxLength → varchar(N)).
            migrationBuilder.CreateTable(
                name: "SupportEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Timestamp = table.Column<DateTime>(nullable: false),
                    Level = table.Column<int>(nullable: false),
                    EventType = table.Column<string>(maxLength: 60, nullable: false),
                    Message = table.Column<string>(maxLength: 8000, nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: true),
                    WorkflowName = table.Column<string>(maxLength: 200, nullable: true),
                    ExecutionId = table.Column<Guid>(nullable: true),
                    ExecutionShort = table.Column<string>(maxLength: 8, nullable: true),
                    StepId = table.Column<string>(maxLength: 120, nullable: true),
                    StepLabel = table.Column<string>(maxLength: 200, nullable: true),
                    ActivityType = table.Column<string>(maxLength: 60, nullable: true),
                    UserName = table.Column<string>(maxLength: 200, nullable: true),
                    UserId = table.Column<Guid>(nullable: true),
                    TraceId = table.Column<string>(maxLength: 32, nullable: true),
                    SpanId = table.Column<string>(maxLength: 16, nullable: true),
                    PropertiesJson = table.Column<string>(maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_EventType_Timestamp",
                table: "SupportEvents",
                columns: new[] { "EventType", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_ExecutionId_Timestamp",
                table: "SupportEvents",
                columns: new[] { "ExecutionId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_Level_Timestamp",
                table: "SupportEvents",
                columns: new[] { "Level", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_Timestamp",
                table: "SupportEvents",
                column: "Timestamp",
                descending: new[] { true });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_WorkflowName_Timestamp",
                table: "SupportEvents",
                columns: new[] { "WorkflowName", "Timestamp" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportEvents");
        }
    }
}
