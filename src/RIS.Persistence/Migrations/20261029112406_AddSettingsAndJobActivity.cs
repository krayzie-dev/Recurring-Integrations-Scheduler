using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RIS.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsAndJobActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobActivity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Group = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    LastWorkUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastWorkSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobActivity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobActivity_Group_Name",
                table: "JobActivity",
                columns: new[] { "Group", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobActivity");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
