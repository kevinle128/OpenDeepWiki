using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryRemoteIdentityAndBranchLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryId",
                table: "RepositoryGenerationLocks");

            migrationBuilder.AddColumn<string>(
                name: "BranchId",
                table: "RepositoryGenerationLocks",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultBranch",
                table: "Repositories",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderBaseUrl",
                table: "Repositories",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderRepositoryId",
                table: "Repositories",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "IncrementalUpdateTasks",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryGenerationLocks_BranchId",
                table: "RepositoryGenerationLocks",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryId_BranchId",
                table: "RepositoryGenerationLocks",
                columns: new[] { "RepositoryId", "BranchId" },
                unique: true,
                filter: "\"BranchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryScope",
                table: "RepositoryGenerationLocks",
                column: "RepositoryId",
                unique: true,
                filter: "\"BranchId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_RemoteIdentity",
                table: "Repositories",
                columns: new[] { "Provider", "ProviderBaseUrl", "ProviderRepositoryId" },
                unique: true,
                filter: "\"ProviderRepositoryId\" IS NOT NULL AND NOT \"IsDeleted\"");

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryGenerationLocks_RepositoryBranches_BranchId",
                table: "RepositoryGenerationLocks",
                column: "BranchId",
                principalTable: "RepositoryBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryGenerationLocks_RepositoryBranches_BranchId",
                table: "RepositoryGenerationLocks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryGenerationLocks_BranchId",
                table: "RepositoryGenerationLocks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryId_BranchId",
                table: "RepositoryGenerationLocks");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryScope",
                table: "RepositoryGenerationLocks");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_RemoteIdentity",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "RepositoryGenerationLocks");

            migrationBuilder.DropColumn(
                name: "DefaultBranch",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "ProviderBaseUrl",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "ProviderRepositoryId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "IncrementalUpdateTasks");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryGenerationLocks_RepositoryId",
                table: "RepositoryGenerationLocks",
                column: "RepositoryId",
                unique: true);
        }
    }
}
