using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditUsernameAndIpAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "AuditLog",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "AuditLog",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_IpAddress_Timestamp",
                table: "AuditLog",
                columns: new[] { "IpAddress", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Username_Timestamp",
                table: "AuditLog",
                columns: new[] { "Username", "Timestamp" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLog_IpAddress_Timestamp",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_Username_Timestamp",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "AuditLog");
        }
    }
}
