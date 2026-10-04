using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddGitConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitConnectionId",
                table: "Repositories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GitConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedServerUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProtectedToken = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastValidatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastValidationErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitConnections_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GitConnectionAuditEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    GitConnectionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitConnectionAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitConnectionAuditEvents_GitConnections_GitConnectionId",
                        column: x => x.GitConnectionId,
                        principalTable: "GitConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_GitConnectionId",
                table: "Repositories",
                column: "GitConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_GitConnectionAuditEvents_ActorUserId_CreatedAt",
                table: "GitConnectionAuditEvents",
                columns: new[] { "ActorUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitConnectionAuditEvents_GitConnectionId_CreatedAt",
                table: "GitConnectionAuditEvents",
                columns: new[] { "GitConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitConnections_CreatedByUserId",
                table: "GitConnections",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GitConnections_Identity",
                table: "GitConnections",
                columns: new[] { "Provider", "NormalizedServerUrl", "ExternalAccountId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Repositories_GitConnections_GitConnectionId",
                table: "Repositories",
                column: "GitConnectionId",
                principalTable: "GitConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Repositories_GitConnections_GitConnectionId",
                table: "Repositories");

            migrationBuilder.DropTable(
                name: "GitConnectionAuditEvents");

            migrationBuilder.DropTable(
                name: "GitConnections");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_GitConnectionId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "GitConnectionId",
                table: "Repositories");
        }
    }
}
