using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Tests;

/// <summary>
/// Real SQLite file in the test scratch directory. Constraint, foreign key, and transaction tests need it:
/// the EF InMemory provider ignores filtered unique indexes, foreign keys, and write conflicts.
/// </summary>
internal sealed class SqliteScratchDatabase : IDisposable
{
    private SqliteScratchDatabase(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static async Task<SqliteScratchDatabase> CreateAsync()
    {
        var database = new SqliteScratchDatabase(
            System.IO.Path.Combine(TestPaths.ScratchRoot, $"scratch-{Guid.NewGuid():N}.db"));
        await using var context = database.Open();
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new User { Id = "user-1", Name = "user-1", Email = "user-1@example.com" });
        context.Users.Add(new User { Id = "user-2", Name = "user-2", Email = "user-2@example.com" });
        await context.SaveChangesAsync();
        return database;
    }

    public ScratchContext Open()
        => new(new DbContextOptionsBuilder<ScratchContext>().UseSqlite($"Data Source={Path}").Options);

    public void Dispose()
    {
        SqlitePools.Release(Path);
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

internal sealed class ScratchContext(DbContextOptions<ScratchContext> options) : MasterDbContext(options);
