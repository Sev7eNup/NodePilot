using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Credentials",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // The structured DropColumnOperation would make EF rebuild the table on
                // SQLite, which needs the PREVIOUS migration's Designer model — and the
                // preceding migration (AddNotificationRouteConditionExpression) is
                // hand-authored without one. SQLite >= 3.35 drops plain nullable columns
                // natively, so raw SQL sidesteps the rebuild entirely (relevant only for
                // the test round-trip; SQLite is the test-only provider).
                migrationBuilder.Sql(@"ALTER TABLE ""Credentials"" DROP COLUMN ""ExpiresAt"";");
            }
            else
            {
                migrationBuilder.DropColumn(
                    name: "ExpiresAt",
                    table: "Credentials");
            }
        }
    }
}
