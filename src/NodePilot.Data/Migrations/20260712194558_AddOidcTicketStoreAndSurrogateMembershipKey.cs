using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcTicketStoreAndSurrogateMembershipKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var storeTypes = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => new
                {
                    String64 = "nvarchar(64)", Binary = "varbinary(max)", DateTime = "datetime2",
                },
                "Npgsql.EntityFrameworkCore.PostgreSQL" => new
                {
                    String64 = "character varying(64)", Binary = "bytea",
                    DateTime = "timestamp with time zone",
                },
                "Microsoft.EntityFrameworkCore.Sqlite" => new
                {
                    String64 = "TEXT", Binary = "BLOB", DateTime = "TEXT",
                },
                _ => throw new NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };

            migrationBuilder.CreateTable(
                name: "OidcLoginTickets",
                columns: table => new
                {
                    Id = table.Column<string>(type: storeTypes.String64, maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<byte[]>(type: storeTypes.Binary, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OidcLoginTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OidcLoginTickets_ExpiresAt",
                table: "OidcLoginTickets",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OidcLoginTickets");

        }
    }
}
