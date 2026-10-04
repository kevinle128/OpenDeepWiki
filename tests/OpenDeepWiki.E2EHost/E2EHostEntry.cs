using System.Runtime.InteropServices;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// Starts the application on a fixed loopback port with a temporary database. Usage:
/// <c>OpenDeepWiki.E2EHost --port 4311 --work-dir /tmp/opendeepwiki-e2e-4311</c>.
/// The work directory is created empty at start and deleted at shutdown. Its name must start with
/// "opendeepwiki-e2e" so the host can never delete anything else.
/// </summary>
internal static class E2EHostEntry
{
    private const string WorkDirPrefix = "opendeepwiki-e2e";

    public static async Task<int> Main(string[] args)
    {
        var port = int.Parse(ReadOption(args, "--port") ?? "4311");
        var workDir = Path.GetFullPath(ReadOption(args, "--work-dir")
            ?? Path.Combine(Path.GetTempPath(), $"{WorkDirPrefix}-{port}"));

        if (!Path.GetFileName(workDir).StartsWith(WorkDirPrefix, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"E2E host: the work directory name must start with '{WorkDirPrefix}'.");
            return 2;
        }

        var refusal = FindForeignDatabaseSetting();
        if (refusal is not null)
        {
            Console.Error.WriteLine(refusal);
            return 2;
        }

        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        Directory.CreateDirectory(workDir);
        Directory.SetCurrentDirectory(workDir);
        ConfigureEnvironment(workDir);

        var log = new RequestLog();
        var provider = new FakeGitProviderHandler();
        await using var factory = new E2EAppFactory(workDir, log, provider);
        factory.UseKestrel(options => options.ListenLocalhost(port));
        factory.StartServer();
        Console.WriteLine($"E2E host ready on http://localhost:{port} (work directory {workDir})");

        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });
        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await factory.DisposeAsync();
        Directory.Delete(workDir, recursive: true);
        Console.WriteLine("E2E host stopped.");
        return 0;
    }

    private static void ConfigureEnvironment(string workDir)
    {
        var settings = new Dictionary<string, string>
        {
            ["Database__Type"] = "sqlite",
            ["ConnectionStrings__Default"] = $"Data Source={Path.Combine(workDir, "e2e.db")}",
            ["DataProtection__KeyRingPath"] = Path.Combine(workDir, "keys"),
            ["REPOSITORIES_DIRECTORY"] = Path.Combine(workDir, "repositories"),
            ["Logging__LogDirectory"] = Path.Combine(workDir, "logs"),
            ["Jwt__SecretKey"] = "e2e-only-signing-key-0123456789-abcdef-0123456789",
            ["IncrementalUpdate__Enabled"] = "false",
            ["WikiGenerator__MaxConcurrentGenerations"] = "16",
            ["GitProviders__AllowedPrivateHosts__0"] = "gitlab.corp-allowed.test",
            ["MCP_ENABLED"] = "false",
        };

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        // Plain variables that the application also reads must not point at a real database.
        Environment.SetEnvironmentVariable("DB_TYPE", null);
        Environment.SetEnvironmentVariable("CONNECTION_STRING", null);
    }

    /// <summary>
    /// The application loads a .env file from the working directory, the application directory and the home
    /// directory after the environment. A file that names a database could redirect the migrations to real data,
    /// so the host refuses to start. The message names the file and never prints its content.
    /// </summary>
    private static string? FindForeignDatabaseSetting()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".env"),
        };

        var keys = new[] { "ConnectionStrings", "Database__", "Database:", "DB_TYPE", "CONNECTION_STRING" };
        foreach (var candidate in candidates.Where(File.Exists).Distinct())
        {
            if (File.ReadLines(candidate).Any(line => keys.Any(key => line.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase))))
            {
                return $"E2E host: refusing to start because {candidate} sets a database. Move it away or run from another directory.";
            }
        }

        return null;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
