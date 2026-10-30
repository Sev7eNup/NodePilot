using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackExternalGroupObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GroupsObservedAt",
                table: "ExternalIdentities",
                nullable: true);

            // Existing identities reject group claims issued before this upgrade.
            migrationBuilder.Sql(ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                ? "UPDATE \"ExternalIdentities\" SET \"GroupsObservedAt\" = SYSUTCDATETIME()"
                : "UPDATE \"ExternalIdentities\" SET \"GroupsObservedAt\" = CURRENT_TIMESTAMP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupsObservedAt",
                table: "ExternalIdentities");
        }
    }
}
