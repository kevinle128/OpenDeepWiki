namespace OpenDeepWiki.Tests;

/// <summary>
/// Directory for temporary test artifacts such as SQLite files and key rings.
/// </summary>
internal static class TestPaths
{
    public static string ScratchRoot { get; } = EnsureDirectory(
        Environment.GetEnvironmentVariable("OPENDEEPWIKI_TEST_SCRATCH")
        ?? Path.Combine(Path.GetTempPath(), "opendeepwiki-tests"));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
