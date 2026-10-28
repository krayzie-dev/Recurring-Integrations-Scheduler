using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RIS.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Role = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Member = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Sid = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleMembers_Role_Sid",
                table: "RoleMembers",
                columns: new[] { "Role", "Sid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleMembers");
        }
    }
}
