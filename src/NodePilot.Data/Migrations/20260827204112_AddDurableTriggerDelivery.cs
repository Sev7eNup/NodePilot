using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableTriggerDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TriggerDeliveryCheckpoints",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggerNodeId = table.Column<string>(maxLength: 200, nullable: false),
                    TriggerType = table.Column<string>(maxLength: 100, nullable: false),
                    ConfigurationHash = table.Column<string>(maxLength: 128, nullable: false),
                    Position = table.Column<string>(nullable: false),
                    Version = table.Column<string>(maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerDeliveryCheckpoints", x => new { x.WorkflowId, x.TriggerNodeId });
                    table.ForeignKey(
                        name: "FK_TriggerDeliveryCheckpoints_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TriggerDeliveryReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggerNodeId = table.Column<string>(maxLength: 200, nullable: false),
                    TriggerType = table.Column<string>(maxLength: 100, nullable: false),
                    EventKey = table.Column<string>(maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(maxLength: 40, nullable: false),
                    ExecutionId = table.Column<Guid>(nullable: true),
                    ReceivedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerDeliveryReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TriggerDeliveryReceipts_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerDeliveryReceipts_ReceivedAt",
                table: "TriggerDeliveryReceipts",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerDeliveryReceipts_WorkflowId_TriggerNodeId_EventKey",
                table: "TriggerDeliveryReceipts",
                columns: new[] { "WorkflowId", "TriggerNodeId", "EventKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriggerDeliveryCheckpoints");

            migrationBuilder.DropTable(
                name: "TriggerDeliveryReceipts");
        }
    }
}
