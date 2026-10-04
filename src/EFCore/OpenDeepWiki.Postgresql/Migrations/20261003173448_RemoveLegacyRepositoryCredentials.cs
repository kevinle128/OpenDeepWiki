using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    /// <summary>
    /// Contract step: drops the legacy repository credential columns. Inactive until the contract release;
    /// see the designer file. Do not run it before the backfill, one full update cycle, and verified backups.
    /// </summary>
    public partial class RemoveLegacyRepositoryCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthAccount",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "AuthPassword",
                table: "Repositories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthAccount",
                table: "Repositories",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthPassword",
                table: "Repositories",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }
    }
}
