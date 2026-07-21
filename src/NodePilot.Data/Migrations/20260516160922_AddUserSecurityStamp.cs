using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provider-agnostic: type omitted so EF maps Int32 → Postgres `integer` /
            // SqlServer `int` automatically. defaultValue=0 because the column is NOT NULL
            // and existing rows pre-date the security-stamp introduction; the first login
            // mints a JWT carrying `np_secstamp=0` and subsequent role/active toggles
            // bump from there. See User.SecurityStamp + AuthSessionIssuer / TokenValidityMiddleware.
            migrationBuilder.AddColumn<int>(
                name: "SecurityStamp",
                table: "Users",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}
