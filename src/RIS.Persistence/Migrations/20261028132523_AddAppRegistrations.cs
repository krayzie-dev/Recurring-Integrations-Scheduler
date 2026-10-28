using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RIS.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppRegistration",
                table: "Connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Tenant = table.Column<string>(type: "TEXT", nullable: false),
                    Authority = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthMode = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedUserPassword = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRegistrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppRegistrations_Name",
                table: "AppRegistrations",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppRegistrations");

            migrationBuilder.DropColumn(
                name: "AppRegistration",
                table: "Connections");
        }
    }
}
