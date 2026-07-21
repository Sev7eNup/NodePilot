using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalVariableFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "GlobalVariables",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000002"));

            migrationBuilder.CreateTable(
                name: "GlobalVariableFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    Path = table.Column<string>(maxLength: 800, nullable: false),
                    Depth = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalVariableFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlobalVariableFolders_GlobalVariableFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "GlobalVariableFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "GlobalVariableFolders",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Depth", "Name", "ParentFolderId", "Path" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000002"), new DateTime(2020, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "Root", null, "/" });

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariables_FolderId",
                table: "GlobalVariables",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableFolders_ParentFolderId",
                table: "GlobalVariableFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableFolders_ParentFolderId_Name",
                table: "GlobalVariableFolders",
                columns: new[] { "ParentFolderId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GlobalVariables_GlobalVariableFolders_FolderId",
                table: "GlobalVariables",
                column: "FolderId",
                principalTable: "GlobalVariableFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GlobalVariables_GlobalVariableFolders_FolderId",
                table: "GlobalVariables");

            migrationBuilder.DropTable(
                name: "GlobalVariableFolders");

            migrationBuilder.DropIndex(
                name: "IX_GlobalVariables_FolderId",
                table: "GlobalVariables");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "GlobalVariables");
        }
    }
}
