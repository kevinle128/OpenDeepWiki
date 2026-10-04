using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Infrastructure;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Sqlite;
using Xunit;

namespace OpenDeepWiki.Tests.Infrastructure;

/// <summary>
/// Verifies that a new database (EF model) and an upgraded database (DbInitializer DDL)
/// end up with the same Git connection schema on SQLite and PostgreSQL.
/// </summary>
public sealed class DbInitializerGitConnectionTests : IDisposable
{
    private static readonly string[] GitConnectionTables =
    [
        "GitConnections", "GitConnectionAuditEvents", "Repositories", "RepositoryGenerationLocks", "IncrementalUpdateTasks",
        "GitCredentialMigrationRecords"
    ];

    private readonly List<string> _sqlitePaths = [];

    public void Dispose()
    {
        foreach (var path in _sqlitePaths)
        {
            SqlitePools.Release(path);
        }

        foreach (var path in _sqlitePaths.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    // ---- SQLite ----

    [Fact]
    public async Task Sqlite_NewDatabase_CreatesGitConnectionSchemaAndInitializerIsIdempotent()
    {
        var dbPath = NewSqlitePath();
        await RunSqliteInitializerAsync(dbPath);
        var first = await DescribeSqliteAsync(dbPath);

        await RunSqliteInitializerAsync(dbPath);
        var second = await DescribeSqliteAsync(dbPath);

        Assert.Contains(first, line => line.StartsWith("column|GitConnections|ProtectedToken|"));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Sqlite_UpgradedDatabase_MatchesNewDatabaseSchema()
    {
        var freshPath = NewSqlitePath();
        await RunSqliteInitializerAsync(freshPath);

        var upgradedPath = await CreateUpgradedSqliteDatabaseAsync();

        var fresh = await DescribeSqliteAsync(freshPath);
        var upgraded = await DescribeSqliteAsync(upgradedPath);
        Assert.Equal(fresh, upgraded);
    }

    [Fact]
    public async Task Sqlite_UpgradedDatabase_KeepsExistingRepositoriesAndEnforcesIdentityAndRestrict()
    {
        var dbPath = NewSqlitePath();
        await using (var legacy = new LegacySqliteContext(SqliteOptions<LegacySqliteContext>(dbPath)))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            legacy.Repositories.Add(new Repository
            {
                Id = "repo-1",
                OwnerUserId = "user-1",
                GitUrl = "https://github.com/acme/widgets",
                OrgName = "acme",
                RepoName = "widgets"
            });
            await legacy.SaveChangesAsync();
        }

        await RunSqliteInitializerAsync(dbPath);

        await using var context = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath));
        var repository = await context.Repositories.SingleAsync(item => item.Id == "repo-1");
        Assert.Null(repository.GitConnectionId);

        context.GitConnections.Add(NewConnection("c1", "1001"));
        await context.SaveChangesAsync();
        context.GitConnections.Add(NewConnection("c2", "1001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        repository = await context.Repositories.SingleAsync(item => item.Id == "repo-1");
        repository.GitConnectionId = "c1";
        await context.SaveChangesAsync();

        // A fresh context does not track the repository, so only the database foreign key protects it.
        await using var deleting = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath));
        deleting.GitConnections.Remove(await deleting.GitConnections.SingleAsync(item => item.Id == "c1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => deleting.SaveChangesAsync());
    }

    [Fact]
    public async Task Sqlite_TableWithoutDisplayNameAndStamp_GainsBothColumnsWithoutDrift()
    {
        var path = NewSqlitePath();
        await RunSqliteInitializerAsync(path);
        var expected = await DescribeSqliteAsync(path);
        await using (var context = new SqliteDbContext(SqliteOptions<SqliteDbContext>(path)))
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE GitConnections DROP COLUMN DisplayName");
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE GitConnections DROP COLUMN ConcurrencyStamp");
        }

        await RunSqliteInitializerAsync(path);

        Assert.Equal(expected, await DescribeSqliteAsync(path));
        Assert.Contains(expected, line => line.StartsWith("column|GitConnections|ConcurrencyStamp|"));
        Assert.Contains(expected, line => line.StartsWith("column|GitConnections|DisplayName|"));
    }

    [Fact]
    public async Task Sqlite_GitConnectionMigration_OnLegacyDatabase_MatchesNewDatabaseSchema()
    {
        var freshPath = NewSqlitePath();
        await RunSqliteInitializerAsync(freshPath);
        var migratedPath = NewSqlitePath();
        await using (var legacy = new LegacySqliteContext(SqliteOptions<LegacySqliteContext>(migratedPath)))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await using (var context = new SqliteDbContext(SqliteOptions<SqliteDbContext>(migratedPath)))
        {
            await MarkEarlierMigrationsAppliedAsync(context, quote: "\"", textType: "TEXT");
            await context.Database.MigrateAsync();
        }

        var fresh = await DescribeSqliteAsync(freshPath);
        var migrated = await DescribeSqliteAsync(migratedPath);
        Assert.Contains(fresh, line => line.StartsWith("column|GitConnections|"));
        Assert.Equal(fresh, migrated);
    }

    [Fact]
    public async Task Sqlite_ProtectedToken_StoresSixteenKilobytePayload()
    {
        var dbPath = NewSqlitePath();
        await RunSqliteInitializerAsync(dbPath);
        var payload = new string('p', 16 * 1024);

        await using (var context = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath)))
        {
            context.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            var connection = NewConnection("c1", "1001");
            connection.ProtectedToken = payload;
            context.GitConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        await using var verification = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath));
        Assert.Equal(payload, (await verification.GitConnections.SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Sqlite_MigrationRecords_ArePerRepositoryAndFollowTheRepositoryWhenItIsRemoved()
    {
        var dbPath = NewSqlitePath();
        await RunSqliteInitializerAsync(dbPath);
        await using (var seed = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath)))
        {
            seed.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            seed.Repositories.Add(new Repository { Id = "repo-1", OwnerUserId = "user-1", GitUrl = "https://github.com/a/b", OrgName = "a", RepoName = "b" });
            seed.GitCredentialMigrationRecords.Add(NewRecord("rec-1", "repo-1"));
            await seed.SaveChangesAsync();
        }

        await using var context = new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath));
        context.GitCredentialMigrationRecords.Add(NewRecord("rec-2", "repo-1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.GitCredentialMigrationRecords.Add(NewRecord("rec-3", "missing"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        await context.Database.ExecuteSqlRawAsync("DELETE FROM Repositories WHERE Id = 'repo-1'");
        Assert.Empty(await context.GitCredentialMigrationRecords.ToListAsync());
    }

    // ---- PostgreSQL ----

    [PostgresFact]
    public async Task Postgres_UpgradedDatabase_MatchesNewDatabaseSchema()
    {
        await using var fresh = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(fresh.ConnectionString);

        await using var upgraded = await PostgresTestDatabase.CreateAsync();
        await using (var legacy = new LegacyPostgresContext(PostgresOptions<LegacyPostgresContext>(upgraded.ConnectionString)))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await RunPostgresInitializerAsync(upgraded.ConnectionString);
        var afterFirstRun = await DescribePostgresAsync(upgraded.ConnectionString);
        await RunPostgresInitializerAsync(upgraded.ConnectionString);
        var afterSecondRun = await DescribePostgresAsync(upgraded.ConnectionString);

        var freshSchema = await DescribePostgresAsync(fresh.ConnectionString);
        Assert.Contains(freshSchema, line => line.StartsWith("column|GitConnections|ProtectedToken|text"));
        Assert.Equal(freshSchema, afterFirstRun);
        Assert.Equal(freshSchema, afterSecondRun);
    }

    [PostgresFact]
    public async Task Postgres_UpgradedDatabase_EnforcesIdentityRestrictAndLargePayload()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var legacy = new LegacyPostgresContext(PostgresOptions<LegacyPostgresContext>(database.ConnectionString)))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            legacy.Repositories.Add(new Repository
            {
                Id = "repo-1",
                OwnerUserId = "user-1",
                GitUrl = "https://github.com/acme/widgets",
                OrgName = "acme",
                RepoName = "widgets"
            });
            await legacy.SaveChangesAsync();
        }

        await RunPostgresInitializerAsync(database.ConnectionString);

        await using var context = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        var payload = new string('p', 16 * 1024);
        var connection = NewConnection("c1", "1001");
        connection.ProtectedToken = payload;
        context.GitConnections.Add(connection);
        await context.SaveChangesAsync();
        context.GitConnections.Add(NewConnection("c2", "1001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        Assert.Equal(payload, (await context.GitConnections.SingleAsync()).ProtectedToken);
        var repository = await context.Repositories.SingleAsync();
        Assert.Null(repository.GitConnectionId);
        repository.GitConnectionId = "c1";
        await context.SaveChangesAsync();

        // A fresh context does not track the repository, so only the database foreign key protects it.
        await using var deleting = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        deleting.GitConnections.Remove(await deleting.GitConnections.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => deleting.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Postgres_GitConnectionMigration_OnLegacyDatabase_MatchesNewDatabaseSchema()
    {
        await using var fresh = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(fresh.ConnectionString);
        await using var migrated = await PostgresTestDatabase.CreateAsync();
        await using (var legacy = new LegacyPostgresContext(PostgresOptions<LegacyPostgresContext>(migrated.ConnectionString)))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await using (var context = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(migrated.ConnectionString)))
        {
            await MarkEarlierMigrationsAppliedAsync(context, quote: "\"", textType: "character varying(150)");
            await context.Database.MigrateAsync();
        }

        var freshSchema = await DescribePostgresAsync(fresh.ConnectionString);
        var migratedSchema = await DescribePostgresAsync(migrated.ConnectionString);
        Assert.Contains(freshSchema, line => line.StartsWith("column|GitConnections|"));
        Assert.Equal(freshSchema, migratedSchema);
    }

    [PostgresFact]
    public async Task Postgres_ConcurrentUpdates_AreReportedForDocumentation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(database.ConnectionString);
        await using (var seed = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString)))
        {
            seed.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            seed.GitConnections.Add(NewConnection("c1", "1001"));
            await seed.SaveChangesAsync();
        }

        await using var first = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        await using var second = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        var firstCopy = await first.GitConnections.SingleAsync();
        var secondCopy = await second.GitConnections.SingleAsync();
        firstCopy.AccountName = "first";
        secondCopy.AccountName = "second";
        await first.SaveChangesAsync();

        // PostgreSQL has no automatic rowversion for the nullable Version column, so the last write wins.
        await second.SaveChangesAsync();
        await using var verification = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        Assert.Equal("second", (await verification.GitConnections.SingleAsync()).AccountName);
    }

    [PostgresFact]
    public async Task Postgres_TableWithoutDisplayNameAndStamp_GainsBothColumnsWithoutDrift()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(database.ConnectionString);
        var expected = await DescribePostgresAsync(database.ConnectionString);
        await using (var context = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString)))
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"GitConnections\" DROP COLUMN \"DisplayName\"");
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"GitConnections\" DROP COLUMN \"ConcurrencyStamp\"");
        }

        await RunPostgresInitializerAsync(database.ConnectionString);

        Assert.Equal(expected, await DescribePostgresAsync(database.ConnectionString));
        Assert.Contains(expected, line => line.StartsWith("column|GitConnections|ConcurrencyStamp|"));
    }

    [PostgresFact]
    public async Task Postgres_ConcurrencyStamp_RejectsTheSecondWriter()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(database.ConnectionString);
        await using (var seed = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString)))
        {
            seed.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            seed.GitConnections.Add(NewConnection("c1", "1001"));
            await seed.SaveChangesAsync();
        }

        await using var first = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        await using var second = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        var firstCopy = await first.GitConnections.SingleAsync();
        var secondCopy = await second.GitConnections.SingleAsync();
        firstCopy.DisplayName = "first";
        firstCopy.ConcurrencyStamp = Guid.NewGuid().ToString();
        secondCopy.DisplayName = "second";
        secondCopy.ConcurrencyStamp = Guid.NewGuid().ToString();
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verification = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        Assert.Equal("first", (await verification.GitConnections.SingleAsync()).DisplayName);
    }

    [PostgresFact]
    public async Task Postgres_SoftDeletedIdentity_IsRestoredWithoutCreatingADuplicate()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(database.ConnectionString);
        await using var context = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        context.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
        var deleted = NewConnection("c1", "1001");
        deleted.MarkAsDeleted();
        context.GitConnections.Add(deleted);
        await context.SaveChangesAsync();

        context.GitConnections.Add(NewConnection("c2", "1001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var match = await context.GitConnections.SingleAsync(connection =>
            connection.Provider == GitProvider.GitHub
            && connection.NormalizedServerUrl == "https://github.com"
            && connection.ExternalAccountId == "1001");
        match.Restore();
        await context.SaveChangesAsync();

        await using var verification = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal("c1", row.Id);
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
    }

    [PostgresFact]
    public async Task Postgres_MigrationRecords_ArePerRepositoryAndFollowTheRepositoryWhenItIsRemoved()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await RunPostgresInitializerAsync(database.ConnectionString);
        await using (var seed = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString)))
        {
            seed.Users.Add(new User { Id = "user-1", Name = "one", Email = "one@example.com" });
            seed.Repositories.Add(new Repository { Id = "repo-1", OwnerUserId = "user-1", GitUrl = "https://github.com/a/b", OrgName = "a", RepoName = "b" });
            seed.GitCredentialMigrationRecords.Add(NewRecord("rec-1", "repo-1"));
            await seed.SaveChangesAsync();
        }

        await using var context = new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(database.ConnectionString));
        context.GitCredentialMigrationRecords.Add(NewRecord("rec-2", "repo-1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.GitCredentialMigrationRecords.Add(NewRecord("rec-3", "missing"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Repositories\" WHERE \"Id\" = 'repo-1'");
        Assert.Empty(await context.GitCredentialMigrationRecords.ToListAsync());
    }

    // ---- helpers ----

    private string NewSqlitePath()
    {
        var path = Path.Combine(TestPaths.ScratchRoot, $"git-connection-init-{Guid.NewGuid():N}.db");
        _sqlitePaths.Add(path);
        return path;
    }

    private async Task<string> CreateUpgradedSqliteDatabaseAsync()
    {
        var path = NewSqlitePath();
        await using (var legacy = new LegacySqliteContext(SqliteOptions<LegacySqliteContext>(path)))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await RunSqliteInitializerAsync(path);
        return path;
    }

    /// <summary>
    /// The migration chain cannot start from an empty database because an older hand-written migration
    /// expects tables that the first migration does not create. Databases are created by EnsureCreated and
    /// DbInitializer, so the test marks every earlier migration as applied and runs only the Git connection ones.
    /// </summary>
    private static async Task MarkEarlierMigrationsAppliedAsync(DbContext context, string quote, string textType)
    {
        await context.Database.ExecuteSqlRawAsync(
            $"CREATE TABLE {quote}__EFMigrationsHistory{quote} (" +
            $"{quote}MigrationId{quote} {textType} NOT NULL CONSTRAINT {quote}PK___EFMigrationsHistory{quote} PRIMARY KEY, " +
            $"{quote}ProductVersion{quote} {(textType == "TEXT" ? "TEXT" : "character varying(32)")} NOT NULL)");

        var migrations = context.Database.GetMigrations().ToList();
        var firstGitConnectionMigration = migrations.FindIndex(id => id.EndsWith("_AddGitConnections", StringComparison.Ordinal));
        Assert.True(firstGitConnectionMigration >= 0);
        Assert.EndsWith("_AddGitCredentialMigrationRecords", migrations[^1]);
        foreach (var migrationId in migrations.Take(firstGitConnectionMigration))
        {
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {quote}__EFMigrationsHistory{quote} ({quote}MigrationId{quote}, {quote}ProductVersion{quote}) " +
                $"VALUES ('{migrationId}', '10.0.8')");
        }
    }

    internal static DbContextOptions<T> SqliteOptions<T>(string path) where T : DbContext
        => new DbContextOptionsBuilder<T>().UseSqlite($"Data Source={path}").Options;

    internal static DbContextOptions<T> PostgresOptions<T>(string connectionString) where T : DbContext
        => new DbContextOptionsBuilder<T>().UseNpgsql(connectionString).Options;

    private static GitCredentialMigrationRecord NewRecord(string id, string repositoryId) => new()
    {
        Id = id,
        RepositoryId = repositoryId,
        State = GitCredentialMigrationState.Failed,
        ErrorCode = "PROVIDER_TIMEOUT",
        AttemptCount = 1,
        LastAttemptAt = DateTime.UtcNow
    };

    private static GitConnection NewConnection(string id, string externalAccountId) => new()
    {
        Id = id,
        Provider = GitProvider.GitHub,
        NormalizedServerUrl = "https://github.com",
        ExternalAccountId = externalAccountId,
        AccountName = "octocat",
        ProtectedToken = "protected-payload",
        CreatedByUserId = "user-1"
    };

    internal static async Task RunSqliteInitializerAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddScoped<IContext>(_ => new SqliteDbContext(SqliteOptions<SqliteDbContext>(dbPath)));
        using var provider = services.BuildServiceProvider();
        await DbInitializer.InitializeAsync(provider);
    }

    internal static async Task RunPostgresInitializerAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddScoped<IContext>(_ => new PostgresqlDbContext(PostgresOptions<PostgresqlDbContext>(connectionString)));
        using var provider = services.BuildServiceProvider();
        await DbInitializer.InitializeAsync(provider);
    }

    internal static async Task<List<string>> DescribeSqliteAsync(string dbPath, params string[] tables)
    {
        var selected = tables.Length == 0 ? GitConnectionTables : tables;
        var lines = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();

        foreach (var table in selected)
        {
            await foreach (var row in QueryAsync(connection,
                $"SELECT name, type, \"notnull\", pk, dflt_value FROM pragma_table_info('{table}')"))
            {
                lines.Add($"column|{table}|{row[0]}|{row[1]}|{row[2]}|{row[3]}|{row[4]}");
            }

            var indexNames = new List<(string Name, string Unique)>();
            await foreach (var row in QueryAsync(connection,
                $"SELECT name, \"unique\" || '/partial=' || partial FROM pragma_index_list('{table}') WHERE name NOT LIKE 'sqlite_autoindex%'"))
            {
                indexNames.Add((row[0], row[1]));
            }

            foreach (var (name, unique) in indexNames)
            {
                var columns = new List<string>();
                await foreach (var row in QueryAsync(connection, $"SELECT name FROM pragma_index_info('{name}') ORDER BY seqno"))
                {
                    columns.Add(row[0]);
                }

                lines.Add($"index|{table}|{name}|{unique}|{string.Join(",", columns)}");
            }

            await foreach (var row in QueryAsync(connection,
                $"SELECT \"from\", \"table\", \"to\", on_delete FROM pragma_foreign_key_list('{table}')"))
            {
                lines.Add($"fk|{table}|{row[0]}|{row[1]}|{row[2]}|{row[3]}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    internal static async Task<List<string>> DescribePostgresAsync(string connectionString, params string[] tables)
    {
        var selected = tables.Length == 0 ? GitConnectionTables : tables;
        var lines = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        async Task Collect(string sql, Func<NpgsqlDataReader, string> format)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("t", selected);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(format(reader));
            }
        }

        await Collect(
            "SELECT table_name, column_name, data_type, is_nullable, column_default, character_maximum_length " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = ANY(@t)",
            r => $"column|{r[0]}|{r[1]}|{r[2]}|{r[3]}|{r[4]}|{r[5]}");
        await Collect(
            "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY(@t)",
            r => $"index|{r[0]}|{r[1]}|{r[2]}");
        await Collect(
            "SELECT conrelid::regclass::text, conname, contype, pg_get_constraintdef(oid) FROM pg_constraint " +
            "WHERE connamespace = 'public'::regnamespace AND contype IN ('p', 'f', 'u') " +
            "AND conrelid::regclass::text = ANY(SELECT '\"' || x || '\"' FROM unnest(@t) AS x)",
            r => $"constraint|{r[0]}|{r[1]}|{r[2]}|{r[3]}");

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async IAsyncEnumerable<string[]> QueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)) ?? string.Empty;
            }

            yield return values;
        }
    }

    /// <summary>
    /// Model of the database before Git connections existed.
    /// </summary>
    private static void RemoveGitConnectionModel(ModelBuilder modelBuilder)
    {
        // Repository identity and branch locks arrived after the first Git connection release.
        var repositoryType = modelBuilder.Entity<Repository>().Metadata;
        foreach (var index in repositoryType.GetIndexes()
                     .Where(item => item.Properties.Any(property => property.Name == nameof(Repository.ProviderRepositoryId)))
                     .ToList())
        {
            repositoryType.RemoveIndex(index);
        }

        modelBuilder.Entity<Repository>().Ignore(item => item.Provider);
        modelBuilder.Entity<Repository>().Ignore(item => item.ProviderBaseUrl);
        modelBuilder.Entity<Repository>().Ignore(item => item.ProviderRepositoryId);
        modelBuilder.Entity<Repository>().Ignore(item => item.DefaultBranch);
        modelBuilder.Entity<IncrementalUpdateTask>().Ignore(item => item.RequestedBy);

        var lockType = modelBuilder.Entity<RepositoryGenerationLock>().Metadata;
        foreach (var index in lockType.GetIndexes().Where(item => item.IsUnique).ToList())
        {
            lockType.RemoveIndex(index);
        }

        modelBuilder.Entity<RepositoryGenerationLock>().Ignore(item => item.Branch);
        modelBuilder.Entity<RepositoryGenerationLock>().Ignore(item => item.BranchId);
        modelBuilder.Entity<RepositoryGenerationLock>().HasIndex(item => item.RepositoryId).IsUnique();

        modelBuilder.Ignore<GitCredentialMigrationRecord>();
        modelBuilder.Entity<Repository>().Ignore(item => item.GitConnection);
        modelBuilder.Entity<Repository>().Ignore(item => item.GitConnectionId);
        modelBuilder.Ignore<GitConnectionAuditEvent>();
        modelBuilder.Ignore<GitConnection>();
    }

    private sealed class LegacySqliteContext(DbContextOptions<LegacySqliteContext> options)
        : MasterDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            RemoveGitConnectionModel(modelBuilder);
        }
    }

    private sealed class LegacyPostgresContext(DbContextOptions<LegacyPostgresContext> options)
        : MasterDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            RemoveGitConnectionModel(modelBuilder);
        }
    }
}

/// <summary>
/// Runs only when a disposable PostgreSQL server is configured through OPENDEEPWIKI_TEST_POSTGRES.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "OPENDEEPWIKI_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = $"Set {ConnectionStringVariable} to a disposable PostgreSQL server to run this test.";
        }
    }
}

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _name;

    private PostgresTestDatabase(string adminConnectionString, string name, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionStringVariable)
            ?? throw new InvalidOperationException("PostgreSQL test server is not configured.");
        var name = $"odw_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false };
        return new PostgresTestDatabase(admin, name, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
