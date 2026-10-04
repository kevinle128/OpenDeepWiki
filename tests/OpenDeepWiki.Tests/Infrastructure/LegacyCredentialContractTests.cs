using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Infrastructure;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Sqlite;
using Xunit;

namespace OpenDeepWiki.Tests.Infrastructure;

/// <summary>
/// The contract step drops the legacy credential columns. It is authored and tested on copies only:
/// nothing in the shipped startup path applies it, and the runtime model still has both columns.
/// </summary>
public sealed class LegacyCredentialContractTests : IDisposable
{
    private const string Legacy = "ghp_CANARY_contract_0123456789";
    private static readonly string[] DroppedColumns = ["AuthAccount", "AuthPassword"];

    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            SqlitePools.Release(path);
        }

        foreach (var path in _paths)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists))
            {
                File.Delete(file);
            }
        }
    }

    // ---- SQLite contract on copies ----

    [Fact]
    public async Task Sqlite_Contract_DropsOnlyTheTwoColumnsAndKeepsEveryRowColumnIndexAndForeignKey()
    {
        var path = await NewSeededSqliteAsync();
        var tables = await ListSqliteTablesAsync(path);
        var before = await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables);
        var rowsBefore = await RowCountsAsync(path, tables);
        var repositoryRowsBefore = await RepositoryRowsWithoutDroppedColumnsAsync(path);

        await ApplySqliteContractAsync(path);

        var after = await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables);
        var expected = before
            .Where(line => !DroppedColumns.Any(column => line.StartsWith($"column|Repositories|{column}|")))
            .ToList();
        Assert.Equal(expected, after);
        Assert.Equal(rowsBefore, await RowCountsAsync(path, tables));
        Assert.Equal(repositoryRowsBefore, await RepositoryRowsWithoutDroppedColumnsAsync(path));
        Assert.Equal("ok", await ScalarAsync(path, "PRAGMA integrity_check"));
        Assert.Equal("0", await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("1", await ScalarAsync(path,
            "SELECT COUNT(*) FROM RepositoryBranches b JOIN Repositories r ON r.Id = b.RepositoryId WHERE r.Id = 'r1'"));
        Assert.Equal("1", await ScalarAsync(path,
            "SELECT COUNT(*) FROM GitCredentialMigrationRecords m JOIN Repositories r ON r.Id = m.RepositoryId WHERE r.Id = 'r1'"));
    }

    [Fact]
    public async Task Sqlite_Contract_KeepsEveryIndexOfTheRepositoriesTableIncludingFilteredOnes()
    {
        var path = await NewSeededSqliteAsync();
        var indexesBefore = await ScalarAsync(path,
            "SELECT group_concat(name || ':' || \"unique\" || ':' || partial, ',') FROM (SELECT * FROM pragma_index_list('Repositories') ORDER BY name)");

        await ApplySqliteContractAsync(path);

        Assert.Equal(indexesBefore, await ScalarAsync(path,
            "SELECT group_concat(name || ':' || \"unique\" || ':' || partial, ',') FROM (SELECT * FROM pragma_index_list('Repositories') ORDER BY name)"));
        Assert.Contains("IX_Repositories_RemoteIdentity", indexesBefore);
        Assert.Contains("IX_Repositories_GitConnectionId", indexesBefore);
    }

    [Theory]
    [InlineData("password-without-connection")]
    [InlineData("account-without-connection")]
    [InlineData("userinfo-url")]
    public async Task Sqlite_Contract_WhenARepositoryStillDependsOnLegacyData_RefusesAndChangesNothing(string kind)
    {
        var path = await NewSeededSqliteAsync();
        var sql = kind switch
        {
            "password-without-connection" => "UPDATE Repositories SET GitConnectionId = NULL WHERE Id = 'r1'",
            "account-without-connection" => "UPDATE Repositories SET GitConnectionId = NULL, AuthPassword = NULL, AuthAccount = 'octocat' WHERE Id = 'r1'",
            _ => "UPDATE Repositories SET GitUrl = 'https://octocat:" + Legacy + "@github.com/acme/widgets.git' WHERE Id = 'r1'"
        };
        await ExecuteAsync(path, sql);
        var tables = await ListSqliteTablesAsync(path);
        var before = await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplySqliteContractAsync(path));

        Assert.DoesNotContain(Legacy, exception.ToString());
        Assert.Equal(before, await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables));
        Assert.Equal("1", await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name = 'AuthPassword'"));
    }

    [Fact]
    public async Task Sqlite_Contract_IgnoresSoftDeletedRepositoriesWhenCheckingTheGate()
    {
        var path = await NewSeededSqliteAsync();
        await ExecuteAsync(path,
            "INSERT INTO Repositories (Id, OwnerUserId, GitUrl, RepoName, OrgName, AuthPassword, IsDeleted, IsPublic, GenerateSkill, ScanDepthMode, Status, StarCount, ForkCount, BookmarkCount, SubscriptionCount, ViewCount, IsDepartmentOwned, CreatedAt) " +
            "VALUES ('gone', 'user-1', 'https://gitee.com/a/b', 'b', 'a', 'secret', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, '2026-01-01')");

        await ApplySqliteContractAsync(path);

        Assert.Equal("0", await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name = 'AuthPassword'"));
    }

    [Fact]
    public async Task Sqlite_Contract_RunTwice_DoesNothingTheSecondTime()
    {
        var path = await NewSeededSqliteAsync();
        await ApplySqliteContractAsync(path);
        var tables = await ListSqliteTablesAsync(path);
        var afterFirst = await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables);

        await ApplySqliteContractAsync(path);

        Assert.Equal(afterFirst, await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables));
    }

    [Fact]
    public async Task Sqlite_ContractBeforeBackup_RestoresTheDualReadSchemaWithTheLegacyCredentialIntact()
    {
        var path = await NewSeededSqliteAsync();
        var backup = NewPath();
        SqlitePools.Release(path);
        File.Copy(path, backup);
        await ApplySqliteContractAsync(path);
        Assert.Equal("0", await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name = 'AuthPassword'"));

        // Restoring means putting the backup file back; the older binary then reads its own columns again.
        SqlitePools.Release(path);
        File.Copy(backup, path, overwrite: true);

        Assert.Equal(Legacy, await ScalarAsync(path, "SELECT AuthPassword FROM Repositories WHERE Id = 'r1'"));
        await using var context = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(path));
        var repository = await context.Repositories.AsNoTracking().SingleAsync(item => item.Id == "r1");
        Assert.Equal(Legacy, repository.AuthPassword);
    }

    [Fact]
    public async Task Sqlite_InitializeAsync_NeverAppliesTheContract()
    {
        var path = NewPath();
        await DbInitializerGitConnectionTests.RunSqliteInitializerAsync(path);
        await DbInitializerGitConnectionTests.RunSqliteInitializerAsync(path);

        Assert.Equal("2", await ScalarAsync(path,
            "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name IN ('AuthAccount', 'AuthPassword')"));
    }

    [RealDatabaseCopyFact]
    public async Task Sqlite_RealFormatCopy_UpgradesThroughExpandAndContractWithoutLosingData()
    {
        var source = Environment.GetEnvironmentVariable(RealDatabaseCopyFactAttribute.PathVariable)!;
        var path = NewPath();
        File.Copy(source, path);
        var tablesBeforeExpand = await ListSqliteTablesAsync(path);
        var rowsBeforeExpand = await RowCountsAsync(path, tablesBeforeExpand);

        await DbInitializerGitConnectionTests.RunSqliteInitializerAsync(path);
        Assert.Equal(rowsBeforeExpand.Where(item => tablesBeforeExpand.Contains(item.Key)).OrderBy(item => item.Key),
            (await RowCountsAsync(path, tablesBeforeExpand)).OrderBy(item => item.Key));

        // Stand in for the backfill: give every repository with legacy credentials a connection.
        await ExecuteAsync(path,
            "INSERT INTO GitConnections (Id, Provider, NormalizedServerUrl, ExternalAccountId, DisplayName, AccountName, ProtectedToken, CreatedByUserId, IsEnabled, ConcurrencyStamp, IsDeleted, CreatedAt) " +
            "SELECT 'copy-connection', 1, 'https://github.com', '1', 'copy', 'copy', 'x', (SELECT Id FROM Users LIMIT 1), 1, 's', 0, '2026-01-01'");
        await ExecuteAsync(path,
            "UPDATE Repositories SET GitConnectionId = 'copy-connection' WHERE (trim(coalesce(AuthPassword,'')) <> '' OR trim(coalesce(AuthAccount,'')) <> '')");
        var tables = await ListSqliteTablesAsync(path);
        var before = await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables);
        var rows = await RowCountsAsync(path, tables);

        await ApplySqliteContractAsync(path);

        var expected = before.Where(line => !DroppedColumns.Any(column => line.StartsWith($"column|Repositories|{column}|"))).ToList();
        Assert.Equal(expected, await DbInitializerGitConnectionTests.DescribeSqliteAsync(path, tables));
        Assert.Equal(rows, await RowCountsAsync(path, tables));
        Assert.Equal("ok", await ScalarAsync(path, "PRAGMA integrity_check"));
        Assert.Equal("0", await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    // ---- EF contract migrations stay inert ----

    [Fact]
    public void ContractMigrations_AreNotDiscoverableByTheMigrator()
    {
        using var sqlite = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(NewPath()));
        using var postgres = new PostgresqlDbContext(DbInitializerGitConnectionTests.PostgresOptions<PostgresqlDbContext>("Host=localhost;Database=never"));

        Assert.DoesNotContain(sqlite.Database.GetMigrations(), id => id.Contains("RemoveLegacyRepositoryCredentials"));
        Assert.DoesNotContain(postgres.Database.GetMigrations(), id => id.Contains("RemoveLegacyRepositoryCredentials"));
        Assert.EndsWith("_AddGitCredentialMigrationRecords", sqlite.Database.GetMigrations().Last());
        Assert.EndsWith("_AddGitCredentialMigrationRecords", postgres.Database.GetMigrations().Last());
    }

    [Fact]
    public void ContractMigrations_DropExactlyTheTwoLegacyColumnsAndRestoreThemOnDown()
    {
        foreach (var migration in new Migration[]
                 {
                     new OpenDeepWiki.Sqlite.Migrations.RemoveLegacyRepositoryCredentials(),
                     new OpenDeepWiki.Postgresql.Migrations.RemoveLegacyRepositoryCredentials()
                 })
        {
            var drops = migration.UpOperations.OfType<DropColumnOperation>().ToList();
            Assert.Equal(DroppedColumns.Order(), drops.Select(item => item.Name).Order());
            Assert.All(drops, item => Assert.Equal("Repositories", item.Table));
            Assert.Equal(2, migration.UpOperations.Count);

            var adds = migration.DownOperations.OfType<AddColumnOperation>().ToList();
            Assert.Equal(DroppedColumns.Order(), adds.Select(item => item.Name).Order());
            Assert.Equal(200, adds.Single(item => item.Name == "AuthAccount").MaxLength);
            Assert.Equal(500, adds.Single(item => item.Name == "AuthPassword").MaxLength);
            Assert.All(adds, item => Assert.True(item.IsNullable));
        }
    }

    [Fact]
    public async Task Sqlite_ContractMigration_AppliedToACopyGivesTheSameSchemaAsTheInitializerContract()
    {
        var viaInitializer = await NewSeededSqliteAsync();
        var viaMigration = await NewSeededSqliteAsync();
        await ApplySqliteContractAsync(viaInitializer);

        await using (var context = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(viaMigration)))
        {
            var migration = new OpenDeepWiki.Sqlite.Migrations.RemoveLegacyRepositoryCredentials();
            var generator = context.GetService<IMigrationsSqlGenerator>();
            var commands = generator.Generate(migration.UpOperations, FinalizeModel(context, migration.TargetModel));
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            foreach (var command in commands)
            {
                await using var execute = connection.CreateCommand();
                execute.CommandText = command.CommandText;
                await execute.ExecuteNonQueryAsync();
            }
        }

        var tables = await ListSqliteTablesAsync(viaInitializer);
        Assert.Equal(
            await DbInitializerGitConnectionTests.DescribeSqliteAsync(viaInitializer, tables),
            await DbInitializerGitConnectionTests.DescribeSqliteAsync(viaMigration, tables));
        Assert.Equal(await RowCountsAsync(viaInitializer, tables), await RowCountsAsync(viaMigration, tables));
    }

    // ---- PostgreSQL contract on a disposable database ----

    [PostgresFact]
    public async Task Postgres_Contract_DropsOnlyTheTwoColumnsAndKeepsEveryRowIndexAndForeignKey()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await SeedPostgresAsync(database.ConnectionString);
        var tables = new[] { "Repositories", "RepositoryBranches", "GitConnections", "GitCredentialMigrationRecords" };
        var before = await DbInitializerGitConnectionTests.DescribePostgresAsync(database.ConnectionString, tables);

        await ApplyPostgresContractAsync(database.ConnectionString);

        var after = await DbInitializerGitConnectionTests.DescribePostgresAsync(database.ConnectionString, tables);
        var expected = before.Where(line => !DroppedColumns.Any(column => line.StartsWith($"column|Repositories|{column}|"))).ToList();
        Assert.Equal(expected, after);
        Assert.Equal("1", await PostgresScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM \"Repositories\" WHERE \"Id\" = 'r1'"));
        Assert.Equal("1", await PostgresScalarAsync(database.ConnectionString,
            "SELECT COUNT(*) FROM \"RepositoryBranches\" b JOIN \"Repositories\" r ON r.\"Id\" = b.\"RepositoryId\""));
    }

    [PostgresFact]
    public async Task Postgres_Contract_WhenARepositoryStillDependsOnLegacyData_RefusesAndChangesNothing()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await SeedPostgresAsync(database.ConnectionString);
        await PostgresExecuteAsync(database.ConnectionString, "UPDATE \"Repositories\" SET \"GitConnectionId\" = NULL WHERE \"Id\" = 'r1'");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyPostgresContractAsync(database.ConnectionString));

        Assert.DoesNotContain(Legacy, exception.ToString());
        Assert.Equal("2", await PostgresScalarAsync(database.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Repositories' AND column_name IN ('AuthAccount', 'AuthPassword')"));
    }

    [PostgresFact]
    public async Task Postgres_Contract_RunTwice_DoesNothingTheSecondTime()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await SeedPostgresAsync(database.ConnectionString);
        await ApplyPostgresContractAsync(database.ConnectionString);
        var tables = new[] { "Repositories" };
        var afterFirst = await DbInitializerGitConnectionTests.DescribePostgresAsync(database.ConnectionString, tables);

        await ApplyPostgresContractAsync(database.ConnectionString);

        Assert.Equal(afterFirst, await DbInitializerGitConnectionTests.DescribePostgresAsync(database.ConnectionString, tables));
    }

    [PostgresFact]
    public async Task Postgres_ContractMigration_AppliedToACopyGivesTheSameSchemaAsTheInitializerContract()
    {
        await using var viaInitializer = await PostgresTestDatabase.CreateAsync();
        await using var viaMigration = await PostgresTestDatabase.CreateAsync();
        await SeedPostgresAsync(viaInitializer.ConnectionString);
        await SeedPostgresAsync(viaMigration.ConnectionString);
        await ApplyPostgresContractAsync(viaInitializer.ConnectionString);

        await using (var context = new PostgresqlDbContext(DbInitializerGitConnectionTests.PostgresOptions<PostgresqlDbContext>(viaMigration.ConnectionString)))
        {
            var migration = new OpenDeepWiki.Postgresql.Migrations.RemoveLegacyRepositoryCredentials();
            var generator = context.GetService<IMigrationsSqlGenerator>();
            foreach (var command in generator.Generate(migration.UpOperations, FinalizeModel(context, migration.TargetModel)))
            {
                await context.Database.ExecuteSqlRawAsync(command.CommandText);
            }
        }

        var tables = new[] { "Repositories", "RepositoryBranches", "GitConnections" };
        Assert.Equal(
            await DbInitializerGitConnectionTests.DescribePostgresAsync(viaInitializer.ConnectionString, tables),
            await DbInitializerGitConnectionTests.DescribePostgresAsync(viaMigration.ConnectionString, tables));
    }

    // ---- recovery drill: the database and the key ring restore together ----

    [Fact]
    public async Task Restore_WithoutTheMatchingKeyRing_FailsAndTheJointRestoreSucceeds()
    {
        var keyRingA = NewKeyRingDirectory();
        var keyRingB = NewKeyRingDirectory();
        var path = NewPath();
        await DbInitializerGitConnectionTests.RunSqliteInitializerAsync(path);
        await using (var seed = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(path)))
        {
            seed.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            seed.GitConnections.Add(new GitConnection
            {
                Id = "c1", Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com", ExternalAccountId = "1",
                DisplayName = "one", AccountName = "octocat", CreatedByUserId = "user-1",
                ProtectedToken = Protector(keyRingA).Protect(Legacy)
            });
            seed.Repositories.Add(new Repository
            {
                Id = "r1", OwnerUserId = "user-1", GitUrl = "https://github.com/acme/widgets.git", OrgName = "acme",
                RepoName = "widgets", GitConnectionId = "c1", Provider = GitProvider.GitHub, ProviderBaseUrl = "https://github.com"
            });
            await seed.SaveChangesAsync();
        }

        SqlitePools.Release(path);
        var restoredDatabase = NewPath();
        File.Copy(path, restoredDatabase);

        // Database restored, key ring lost: reading the credential fails closed with a stable code.
        await using (var withoutRing = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(restoredDatabase)))
        {
            var repository = await withoutRing.Repositories.AsNoTracking().SingleAsync();
            var resolver = new GitCredentialResolver(withoutRing, Protector(keyRingB), NullLogger<GitCredentialResolver>.Instance);
            var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() => resolver.ResolveAsync(repository));
            Assert.Equal(GitCredentialErrorCodes.SecretUnreadable, exception.ErrorCode);
            Assert.DoesNotContain(Legacy, exception.ToString());
        }

        // Database and key ring restored together: the read-only credential path works.
        await using var joint = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(restoredDatabase));
        var restored = await joint.Repositories.AsNoTracking().SingleAsync();
        var credential = await new GitCredentialResolver(joint, Protector(keyRingA), NullLogger<GitCredentialResolver>.Instance)
            .ResolveAsync(restored);
        Assert.Equal(Legacy, credential!.Password);
    }

    // ---- helpers ----

    private static IGitConnectionSecretProtector Protector(string keyRingDirectory)
        => new DataProtectionGitConnectionSecretProtector(
            DataProtectionProvider.Create(new DirectoryInfo(keyRingDirectory), options => options.SetApplicationName("OpenDeepWiki")));

    private string NewKeyRingDirectory()
    {
        var directory = Path.Combine(TestPaths.ScratchRoot, $"keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _paths.Add(directory);
        return directory;
    }

    private string NewPath()
    {
        var path = Path.Combine(TestPaths.ScratchRoot, $"contract-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static IModel FinalizeModel(DbContext context, IModel model)
    {
        if (model is IMutableModel mutable)
        {
            model = mutable.FinalizeModel();
        }

        return model is IConventionModel
            ? context.GetService<IModelRuntimeInitializer>().Initialize(model)
            : model;
    }

    private static Task ApplySqliteContractAsync(string path)
        => ApplyAsync(new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(path)));

    private static Task ApplyPostgresContractAsync(string connectionString)
        => ApplyAsync(new PostgresqlDbContext(DbInitializerGitConnectionTests.PostgresOptions<PostgresqlDbContext>(connectionString)));

    private static async Task ApplyAsync(DbContext context)
    {
        await using (context)
        {
            await DbInitializer.ApplyLegacyCredentialContractAsync(context, CancellationToken.None);
        }
    }

    /// <summary>
    /// Initialized database with one connected repository that still holds its legacy password,
    /// a child branch, a lock, an audit event, a progress record, and a public repository.
    /// </summary>
    private async Task<string> NewSeededSqliteAsync()
    {
        var path = NewPath();
        await DbInitializerGitConnectionTests.RunSqliteInitializerAsync(path);
        await using (var context = new SqliteDbContext(DbInitializerGitConnectionTests.SqliteOptions<SqliteDbContext>(path)))
        {
            Seed(context);
            await context.SaveChangesAsync();
        }

        SqlitePools.Release(path);
        return path;
    }

    private static async Task SeedPostgresAsync(string connectionString)
    {
        await DbInitializerGitConnectionTests.RunPostgresInitializerAsync(connectionString);
        await using var context = new PostgresqlDbContext(DbInitializerGitConnectionTests.PostgresOptions<PostgresqlDbContext>(connectionString));
        Seed(context);
        await context.SaveChangesAsync();
    }

    private static void Seed(MasterDbContext context)
    {
        context.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
        context.GitConnections.Add(new GitConnection
        {
            Id = "c1", Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com", ExternalAccountId = "1",
            DisplayName = "one", AccountName = "octocat", CreatedByUserId = "user-1", ProtectedToken = "protected-payload"
        });
        context.Repositories.Add(new Repository
        {
            Id = "r1", OwnerUserId = "user-1", GitUrl = "https://github.com/acme/widgets.git", OrgName = "acme", RepoName = "widgets",
            AuthAccount = "octocat", AuthPassword = Legacy, GitConnectionId = "c1", Provider = GitProvider.GitHub,
            ProviderBaseUrl = "https://github.com", ProviderRepositoryId = "42", IsPublic = false
        });
        context.Repositories.Add(new Repository
        {
            Id = "r2", OwnerUserId = "user-1", GitUrl = "https://github.com/acme/open.git", OrgName = "acme", RepoName = "open"
        });
        context.RepositoryBranches.Add(new RepositoryBranch { Id = "b1", RepositoryId = "r1", BranchName = "main" });
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = "l1", RepositoryId = "r1", BranchId = "b1", OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
            OwnerId = "t1", Scope = RepositoryGenerationLockScope.Branch
        });
        context.GitConnectionAuditEvents.Add(new GitConnectionAuditEvent
        {
            Id = "e1", GitConnectionId = "c1", EventType = GitConnectionAuditEventType.RepositoryAssigned,
            Outcome = GitConnectionAuditOutcome.Success, RepositoryId = "r1"
        });
        context.GitCredentialMigrationRecords.Add(new GitCredentialMigrationRecord
        {
            Id = "m1", RepositoryId = "r1", State = GitCredentialMigrationState.Migrated, GitConnectionId = "c1",
            AttemptCount = 1, LastAttemptAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
        });
    }

    private static async Task<string[]> ListSqliteTablesAsync(string path)
    {
        var tables = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static async Task<Dictionary<string, long>> RowCountsAsync(string path, IEnumerable<string> tables)
    {
        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            counts[table] = Convert.ToInt64(await ScalarAsync(path, $"SELECT COUNT(*) FROM \"{table}\""));
        }

        return counts;
    }

    private static async Task<string> RepositoryRowsWithoutDroppedColumnsAsync(string path)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('Repositories') WHERE name NOT IN ('AuthAccount', 'AuthPassword') ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText = $"SELECT {string.Join(",", columns.Select(column => $"quote(\"{column}\")"))} FROM Repositories ORDER BY Id";
        var rows = new List<string>();
        await using var rowReader = await select.ExecuteReaderAsync();
        while (await rowReader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, rowReader.FieldCount).Select(rowReader.GetString)));
        }

        return string.Join("\n", rows);
    }

    private static async Task<string> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> PostgresScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task PostgresExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Runs only when OPENDEEPWIKI_TEST_REAL_DB_COPY names a copy of a real database file in the scratch area.
/// The test copies it again before it changes anything. Never point it at a live database.
/// </summary>
public sealed class RealDatabaseCopyFactAttribute : FactAttribute
{
    public const string PathVariable = "OPENDEEPWIKI_TEST_REAL_DB_COPY";

    public RealDatabaseCopyFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip = $"Set {PathVariable} to a copy of a real database file to run this test.";
        }
    }
}
