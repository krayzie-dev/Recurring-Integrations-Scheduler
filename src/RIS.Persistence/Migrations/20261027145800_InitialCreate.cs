using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RIS.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    User = table.Column<string>(type: "TEXT", nullable: true),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityKey = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Before = table.Column<string>(type: "TEXT", nullable: true),
                    After = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    AosUri = table.Column<string>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Group = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduleJson = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PairedJob = table.Column<string>(type: "TEXT", nullable: true),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SftpServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    UseKey = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeyPath = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedKeyPassphrase = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SftpServers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_TimestampUtc",
                table: "Audit",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Name",
                table: "Connections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Group_Name",
                table: "Jobs",
                columns: new[] { "Group", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SftpServers_Name",
                table: "SftpServers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "Connections");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "SftpServers");
        }
    }
}
