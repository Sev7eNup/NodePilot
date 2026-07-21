using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    // Hand-authored (no Designer file): the [DbContext] attribute is what EF's migration
    // discovery filters on — without it the migration is silently skipped by Migrate() and
    // the model/schema drift crashes the first query touching the new column.
    [DbContext(typeof(NodePilotDbContext))]
    [Migration("20260702120000_AddNotificationRouteConditionExpression")]
    public partial class AddNotificationRouteConditionExpression : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConditionExpressionJson",
                table: "NotificationRoutes",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConditionExpressionJson",
                table: "NotificationRoutes");
        }
    }
}
