using Microsoft.Data.Sqlite;

namespace OpenDeepWiki.Tests;

/// <summary>
/// Releases the pooled connections of one SQLite file so the test can read, copy, or delete it.
/// Never call <c>SqliteConnection.ClearAllPools()</c> from a test: xUnit runs test classes in parallel, and the
/// process-wide call disposes pooled handles that another test is opening at that moment. That race surfaces as
/// an <see cref="ObjectDisposedException"/> from <c>SQLitePCL.sqlite3</c> in unrelated tests.
/// The pool key is the connection string, so the string here must match the one the test uses.
/// </summary>
internal static class SqlitePools
{
    public static void Release(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
    }
}
