using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScimGroupDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var storeTypes = migrationBuilder.ActiveProvider switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => new
                {
                    Guid = "uniqueidentifier", String256 = "nvarchar(256)",
                    String384 = "nvarchar(384)", Boolean = "bit", DateTime = "datetime2",
                },
                "Npgsql.EntityFrameworkCore.PostgreSQL" => new
                {
                    Guid = "uuid", String256 = "character varying(256)",
                    String384 = "character varying(384)", Boolean = "boolean",
                    DateTime = "timestamp with time zone",
                },
                "Microsoft.EntityFrameworkCore.Sqlite" => new
                {
                    Guid = "TEXT", String256 = "TEXT", String384 = "TEXT",
                    Boolean = "INTEGER", DateTime = "TEXT",
                },
                _ => throw new NotSupportedException(
                    $"Provider '{migrationBuilder.ActiveProvider}' is not supported."),
            };

            migrationBuilder.CreateTable(
                name: "ScimGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: storeTypes.Guid, nullable: false),
                    Authority = table.Column<string>(type: storeTypes.String384, maxLength: 384, nullable: false),
                    ExternalId = table.Column<string>(type: storeTypes.String384, maxLength: 384, nullable: false),
                    DisplayName = table.Column<string>(type: storeTypes.String256, maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: storeTypes.Boolean, nullable: false),
                    IsTombstoned = table.Column<bool>(type: storeTypes.Boolean, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: storeTypes.DateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroups_Authority_ExternalId",
                table: "ScimGroups",
                columns: new[] { "Authority", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroups_DisplayName",
                table: "ScimGroups",
                column: "DisplayName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScimGroups");
        }
    }
}
